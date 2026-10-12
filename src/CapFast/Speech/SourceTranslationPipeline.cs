using System.Diagnostics;
using System.Threading.Channels;
using CapFast.Core;

namespace CapFast.Speech;

public sealed class SourceTranslationPipeline : IAsyncDisposable
{
    private sealed record QueuedSource(CaptionUpdate Update, TaskCompletionSource Completed, Task EarlierWork,
        long AcceptedAt, double ReceivedAt, long AudioGeneration, string PreviousSource,
        Func<string, CancellationToken, Task<string?>>? DetectSegment,
        Task<MultilingualCaptionResult>? MultilingualWork);
    private readonly Dictionary<long, List<TaskCompletionSource>> _normalWork = new();
    private readonly Channel<QueuedSource> _queue;
    private readonly HashSet<Task> _scheduled = new();
    private readonly HashSet<Task> _receivedFinalWork = new();
    private int _pendingSourceCount;
    private readonly int _capacity;
    private readonly TimeSpan _maxTranslationAge;
    private readonly SemaphoreSlim _detectionSlots = new(2, 2);
    private readonly SemaphoreSlim _translationSlots = new(2, 2);
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly HashSet<string> _accepted = new();
    private readonly Queue<string> _recent = new();
    private readonly Dictionary<long, StreamingSourceSegmenter> _sourceSegments = new();
    private readonly Dictionary<long, string> _previousAcceptedSource = new();
    private readonly HashSet<string> _knownBlockedSource = new(StringComparer.Ordinal);
    private readonly Queue<string> _blockedSourceHistory = new();
    private StreamingSourceSegmenter SourceSegments(long generation)
    {
        if (!_sourceSegments.TryGetValue(generation, out var segments))
        {
            segments = new(); _sourceSegments[generation] = segments;
            if (_sourceSegments.Count > 32) _sourceSegments.Remove(_sourceSegments.Keys.Min());
        }
        return segments;
    }
    private readonly Dictionary<string, string> _utteranceLanguages = new();
    private readonly HashSet<string> _expiredUtterances = new();
    private readonly Queue<string> _utteranceHistory = new();
    private readonly SpeechLanguageAssignment _assignment;
    private readonly Func<string, LanguageAssignment, CancellationToken, Task<(string Primary, string Secondary)>> _translateAssigned;
    private readonly Func<string, string, string?, CancellationToken, Task<MultilingualCaptionResult>>? _translateMultilingual;
    public long AudioGeneration { get { lock (_gate) return _assignment.AudioGeneration; } }
    public sealed record ReceivedBlock(long Generation, long ManualRevision, LanguageAssignment? Assignment, string? LatestInput);
    private long _manualRevision;
    private readonly Dictionary<long, LanguageAssignment> _completedBlocks = new();
    private CancellationTokenSource? _manualWork;
    private readonly Func<string, LanguageAssignment, CancellationToken, Task<(string Primary, string Secondary)>> _translateDrained;
    public ReceivedBlock CaptureReceivedBlock(long generation) { lock (_gate) return new(generation, generation == AudioGeneration ? _manualRevision : -1,
        generation == AudioGeneration ? Assignment : null, generation == AudioGeneration ? _assignment.LatestInput : null); }
    public bool IsReceivedBlockValid(ReceivedBlock block) { lock (_gate) return !_disposed && block.ManualRevision == _manualRevision; }
    public bool IsReceivedBlockCurrent(ReceivedBlock block) { lock (_gate) return block.ManualRevision == _manualRevision && block.Generation == AudioGeneration; }
    public string? FallbackInput(ReceivedBlock block)
    {
        lock (_gate) return block.ManualRevision == _manualRevision && block.Generation == AudioGeneration
            ? _assignment.LatestInput ?? block.LatestInput ?? block.Assignment?.Input
            : block.LatestInput ?? block.Assignment?.Input;
    }
    public bool CanPresentCaption(CaptionUpdate update)
    {
        lock (_gate) return update.IsSpeechBlockDrain ? update.SpeechBlockRevision == _manualRevision : Assignment?.Epoch == update.AssignmentEpoch;
    }
    private void InvalidateManualWork()
    {
        ++_manualRevision; _manualWork?.Cancel(); _manualWork?.Dispose();
        _manualWork = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }
    private string? _hintUtterance;
    private string? _hintLanguage;
    public string? PendingHint => _assignment.PendingHint;
    public string? CaptureHint(string utterance)
    {
        lock (_gate)
        {
            if (_hintUtterance == utterance) return _hintLanguage;
            if (_assignment.PendingHint is not { } hint) return null;
            _hintUtterance ??= utterance;
            _hintLanguage = hint;
            return _hintUtterance == utterance ? hint : null;
        }
    }
    public LanguageAssignment? Assignment => _assignment.Current;
    public event Action<LanguageAssignment?>? AssignmentChanged;
    public bool ObserveAudio(AudioActivity activity, double now)
    {
        lock (_gate)
        {
            var previous = _assignment.Current;
            var generation = _assignment.AudioGeneration;
            _assignment.ObserveAudio(activity, now);
            if (generation != _assignment.AudioGeneration && previous is not null)
            {
                _completedBlocks[generation] = previous;
                if (_completedBlocks.Count > 32) _completedBlocks.Remove(_completedBlocks.Keys.Min());
            }
            if (previous is not null && _assignment.Current is null)
                foreach (var id in _utteranceLanguages.Keys) _expiredUtterances.Add(id);
            return generation != _assignment.AudioGeneration;
        }
    }
    public void ResetForNextUtterance()
    {
        lock (_gate)
        {
            InvalidateManualWork(); _hintUtterance = null; _hintLanguage = null;
            foreach (var id in _utteranceLanguages.Keys) _expiredUtterances.Add(id);
            _assignment.BeginFreshAudioGeneration();
        }
    }
    public void ResetAssignment() { lock (_gate) { InvalidateManualWork(); _hintUtterance = null; _hintLanguage = null; _assignment.Reset(); } }
    public void HintInputLanguage(string code) { lock (_gate) { InvalidateManualWork(); _hintUtterance = null; _hintLanguage = SpeechLanguageAssignment.Normalize(code); _assignment.Hint(code); } }
    private bool _disposed;
    public event Action<CaptionUpdate>? Caption;
    public event Action<string>? Failed;
    public event Action<string>? Diagnostic;
    public event Action<IReadOnlyList<CaptionUpdate>>? BatchStarted;
    public event Action<CaptionUpdate>? SourceAccepted;
    public Task Completion { get; }

    public SourceTranslationPipeline(Func<string, LanguageAssignment, CancellationToken, Task<(string Primary, string Secondary)>> translate,
        CancellationToken cancellationToken = default,
        Func<string, LanguageAssignment, CancellationToken, Task<(string Primary, string Secondary)>>? drainTranslate = null,
        int capacity = 8, TimeSpan? maxTranslationAge = null,
        Func<string, string, string?, CancellationToken, Task<MultilingualCaptionResult>>? translateMultilingual = null)
    {
        _maxTranslationAge = maxTranslationAge ?? TimeSpan.FromSeconds(8);
        if (_maxTranslationAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxTranslationAge));
        _capacity = capacity;
        _translateMultilingual = translateMultilingual;
        _translateAssigned = translate; _translateDrained = drainTranslate ?? translate;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _queue = Channel.CreateBounded<QueuedSource>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _manualWork = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _assignment = new();
        _assignment.Changed += assignment => AssignmentChanged?.Invoke(assignment);
        Completion = ProcessAsync();
    }

    private bool TryEnqueue(CaptionUpdate source, double receivedAt, long audioGeneration,
        Func<string, CancellationToken, Task<string?>>? detectSegment)
    {
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested) return false;
            if (!source.IsFinal || string.IsNullOrWhiteSpace(source.Text) || _accepted.Contains(source.UtteranceId)) return true;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pendingSourceCount >= _capacity) return false;
            source = source with { SpeechBlockRevision = _manualRevision, IsSpeechBlockDrain = true };
            if (!_normalWork.TryGetValue(source.AssignmentEpoch, out var work)) _normalWork[source.AssignmentEpoch] = work = new();
            work.RemoveAll(item => item.Task.IsCompleted);
            var earlier = Task.WhenAll(work.Select(item => item.Task));
            var previousSource = PreviousSource(source.AssignmentEpoch);
            // Start accepted HTTP work here, in source order. A final suffix
            // cannot take both service slots before its queued stable prefix.
            var combined = _translateMultilingual is null ? null : TranslateBoundedAsync(
                source.Text, previousSource, source.SourceLanguage, source.AssignmentEpoch, _manualWork!.Token);
            if (!_queue.Writer.TryWrite(new(source, completed, earlier, Stopwatch.GetTimestamp(), receivedAt,
                audioGeneration, previousSource, detectSegment, combined)))
            {
                if (combined is not null) ObserveFault(combined);
                return false;
            }
            RememberSource(source.AssignmentEpoch, source.Text);
            ++_pendingSourceCount; work.Add(completed);
            if (_normalWork.Count > 32)
                foreach (var epoch in _normalWork.Where(pair => pair.Value.All(item => item.Task.IsCompleted)).Select(pair => pair.Key).ToArray())
                    _normalWork.Remove(epoch);
            _accepted.Add(source.UtteranceId);
            _recent.Enqueue(source.UtteranceId);
            if (_recent.Count > 256) _accepted.Remove(_recent.Dequeue());
            SourceAccepted?.Invoke(source);
            return true;
        }
    }

    private string PreviousSource(long epoch) => _previousAcceptedSource.GetValueOrDefault(epoch) ?? "";

    private static string ShortSource(string text) => string.Join(" ", text.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries).TakeLast(48));

    private void RememberSource(long epoch, string text)
    {
        _previousAcceptedSource[epoch] = ShortSource(text);
        if (_previousAcceptedSource.Count > 32) _previousAcceptedSource.Remove(_previousAcceptedSource.Keys.Min());
    }

    public bool TryAcceptTranscript(CaptionUpdate source, double nowSeconds, long? audioGeneration = null,
        Func<string, CancellationToken, Task<string?>>? detectSegment = null)
    {
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested) return false;
            if (audioGeneration is { } origin && origin != _assignment.AudioGeneration) return true;
            if (_expiredUtterances.Contains(source.UtteranceId)) return true;
            var pendingHint = _assignment.PendingHint;
            var language = pendingHint ?? (_hintUtterance == source.UtteranceId ? _hintLanguage : SpeechLanguageAssignment.Normalize(source.SourceLanguage));
            if (pendingHint is not null) _hintUtterance = source.UtteranceId;
            if (language is null) _utteranceLanguages.TryGetValue(source.UtteranceId, out language);
            if (language is not null && !_utteranceLanguages.ContainsKey(source.UtteranceId))
            {
                _utteranceHistory.Enqueue(source.UtteranceId);
                if (_utteranceHistory.Count > 256)
                {
                    var expired = _utteranceHistory.Dequeue();
                    _utteranceLanguages.Remove(expired); _expiredUtterances.Remove(expired);
                }
            }
            if (language is not null) _utteranceLanguages[source.UtteranceId] = language;
            var assignment = _assignment.Accept(language, nowSeconds);
            if (assignment is null) return true;
            foreach (var segment in SourceSegments(audioGeneration ?? AudioGeneration).Apply(source, nowSeconds))
                if (!TryEnqueue(segment with { UtteranceId = $"{assignment.Epoch}:{segment.UtteranceId}", SourceLanguage = assignment.Input,
                    PrimaryLanguage = assignment.Primary, SecondaryLanguage = assignment.Secondary,
                    AssignmentEpoch = assignment.Epoch }, nowSeconds, audioGeneration ?? AudioGeneration, detectSegment)) return false;
            return true;
        }
    }

    // Finals already received belong to their original audio block, even if
    // classification finishes after its silence boundary. They never assign a new block.
    public async Task CompleteReceivedFinalAsync(CaptionUpdate source, ReceivedBlock block, double now,
        Func<string, CancellationToken, Task<string?>>? detectSegment = null)
    {
        var acceptedAt = Stopwatch.GetTimestamp();
        List<CaptionUpdate> segments;
        List<string> previousSources;
        Task<MultilingualCaptionResult>[] translations;
        LanguageAssignment assigned;
        CancellationToken token;
        Task[] earlierChunks;
        TaskCompletionSource completed;
        lock (_gate)
        {
            if (_disposed || block.ManualRevision != _manualRevision) return;
            var language = SpeechLanguageAssignment.Normalize(source.SourceLanguage);
            if (language is null) return;
            var original = block.Assignment ?? _completedBlocks.GetValueOrDefault(block.Generation);
            if (block.Generation == AudioGeneration)
            {
                var hint = _assignment.PendingHint;
                if (hint is not null) { _hintUtterance = source.UtteranceId; language = hint; }
                else if (_hintUtterance == source.UtteranceId) language = _hintLanguage ?? language;
                var current = _assignment.Accept(language, now);
                original ??= Assignment ?? current;
            }
            if (original is null)
            {
                original = SpeechLanguageAssignment.ForSource(language, -block.Generation - 1);
                _completedBlocks[block.Generation] = original;
                if (_completedBlocks.Count > 32) _completedBlocks.Remove(_completedBlocks.Keys.Min());
            }
            if (_receivedFinalWork.Count >= 32)
            {
                if (block.Generation == AudioGeneration)
                    throw new CaptionSessionException("Translation fell behind the audio. Restart the session to continue.");
                Diagnostic?.Invoke("Google text translation fell behind for an earlier speech block; its unfinished caption is unavailable.");
                return;
            }
            assigned = language == original.Input ? original : SpeechLanguageAssignment.ForSource(language, original.Epoch);
            token = _manualWork!.Token;
            earlierChunks = _normalWork.GetValueOrDefault(assigned.Epoch)?.Select(work => work.Task).ToArray() ?? [];
            segments = SourceSegments(block.Generation).Apply(source, now).Select(segment => segment with
            {
                UtteranceId = $"{assigned.Epoch}:{segment.UtteranceId}", SourceLanguage = assigned.Input,
                PrimaryLanguage = assigned.Primary, SecondaryLanguage = assigned.Secondary,
                AssignmentEpoch = assigned.Epoch, IsSpeechBlockDrain = true, SpeechBlockRevision = block.ManualRevision
            }).Where(segment => _accepted.Add(segment.UtteranceId)).ToList();
            previousSources = new(segments.Count);
            foreach (var segment in segments)
            {
                previousSources.Add(PreviousSource(segment.AssignmentEpoch));
                RememberSource(segment.AssignmentEpoch, segment.Text);
            }
            foreach (var segment in segments) { _recent.Enqueue(segment.UtteranceId); if (_recent.Count > 256) _accepted.Remove(_recent.Dequeue()); }
            if (segments.Count == 0) return;
            translations = _translateMultilingual is null ? [] : segments.Select((segment, index) =>
                TranslateBoundedAsync(segment.Text, previousSources[index], segment.SourceLanguage,
                    segment.AssignmentEpoch, token)).ToArray();
            // Finals share the same block ledger as queued stable chunks. Later
            // sources wait for this final, while another block remains independent.
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_normalWork.TryGetValue(assigned.Epoch, out var work)) _normalWork[assigned.Epoch] = work = new();
            work.RemoveAll(item => item.Task.IsCompleted);
            work.Add(completed);
            _receivedFinalWork.Add(completed.Task);
            if (_normalWork.Count > 32)
                foreach (var epoch in _normalWork.Where(pair => pair.Value.All(item => item.Task.IsCompleted)).Select(pair => pair.Key).ToArray())
                    _normalWork.Remove(epoch);
        }
        using var ageWork = _translateMultilingual is null ? CreateAgeDeadline(acceptedAt, token)
            : CancellationTokenSource.CreateLinkedTokenSource(token);
        var detections = _translateMultilingual is not null || detectSegment is null ? [] : segments.Select(segment =>
            DetectBoundedAsync(detectSegment, segment.Text, ageWork.Token)).ToArray();
        try
        {
            try { await Task.WhenAll(earlierChunks).WaitAsync(ageWork.Token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (ageWork.IsCancellationRequested)
            {
                ReportExpiredTranslation(block.ManualRevision); return;
            }
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                Task<(string Primary, string Secondary)>? work = null;
                try
                {
                    ageWork.Token.ThrowIfCancellationRequested();
                    var routed = segment;
                    var segmentAssignment = assigned;
                    if (_translateMultilingual is not null)
                    {
                        var result = await translations[index].WaitAsync(ageWork.Token);
                        routed = AssignDetectedSegment(segment,
                            TrustedOrDetected(segment.SourceLanguage, result.SourceLanguage), now, block.Generation);
                        segmentAssignment = new(routed.SourceLanguage!, routed.PrimaryLanguage!,
                            routed.SecondaryLanguage!, routed.AssignmentEpoch);
                        var selected = result.For(segmentAssignment);
                        lock (_gate) if (!_disposed && block.ManualRevision == _manualRevision && !_lifetime.IsCancellationRequested)
                            Caption?.Invoke(routed with { Text = selected.Primary, FrenchText = selected.Secondary });
                        continue;
                    }
                    if (detectSegment is not null)
                    {
                        var language = await detections[index].WaitAsync(ageWork.Token);
                        routed = AssignDetectedSegment(segment, language, now, block.Generation);
                        segmentAssignment = new(routed.SourceLanguage!, routed.PrimaryLanguage!,
                            routed.SecondaryLanguage!, routed.AssignmentEpoch);
                    }
                    work = _translateDrained!(segment.Text, segmentAssignment, ageWork.Token);
                    var pair = await work.WaitAsync(ageWork.Token);
                    lock (_gate) if (!_disposed && block.ManualRevision == _manualRevision && !_lifetime.IsCancellationRequested &&
                        !ageWork.IsCancellationRequested)
                        Caption?.Invoke(routed with { Text = pair.Primary, FrenchText = pair.Secondary });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (work is not null) _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    return;
                }
                catch (OperationCanceledException) when (ageWork.IsCancellationRequested)
                {
                    if (work is not null) _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    ReportExpiredTranslation(block.ManualRevision); return;
                }
                catch (Exception error)
                {
                    lock (_gate) if (!_disposed && block.ManualRevision == _manualRevision && !_lifetime.IsCancellationRequested)
                    {
                        if (error is CaptionSessionException { IsTransientTranslation: true } transient)
                            Diagnostic?.Invoke(transient.Message);
                        else if (block.Generation == AudioGeneration)
                            Failed?.Invoke(error is CaptionSessionException safe ? safe.Message : "Google text translation failed. Restart the session to continue.");
                        else Diagnostic?.Invoke("Google text translation failed for an earlier speech block; its unfinished caption is unavailable.");
                    }
                    if (error is not CaptionSessionException { IsTransientTranslation: true }) return;
                }
            }
        }
        finally
        {
            ageWork.Cancel();
            foreach (var detection in detections) ObserveFault(detection);
            foreach (var translation in translations) ObserveFault(translation);
            lock (_gate) { completed.TrySetResult(); _receivedFinalWork.Remove(completed.Task); }
        }
    }

    private async Task ProcessAssignedAsync(QueuedSource first, string text)
    {
        Task<(string Primary, string Secondary)>? work = null;
        Task<MultilingualCaptionResult>? multilingualWork = null;
        Task<string?>? detection = null;
        CancellationToken token = default;
        CancellationTokenSource? ageWork = null;
        try
        {
            var update = first.Update;
            lock (_gate)
            {
                if (_disposed || _lifetime.IsCancellationRequested || update.SpeechBlockRevision != _manualRevision) return;
                token = _manualWork!.Token;
            }
            ageWork = _translateMultilingual is null ? CreateAgeDeadline(first.AcceptedAt, token)
                : CancellationTokenSource.CreateLinkedTokenSource(token);
            if (_translateMultilingual is not null)
            {
                multilingualWork = first.MultilingualWork!;
                var multilingualResult = await multilingualWork.WaitAsync(ageWork.Token);
                await first.EarlierWork.WaitAsync(ageWork.Token);
                update = AssignDetectedSegment(update,
                    TrustedOrDetected(update.SourceLanguage, multilingualResult.SourceLanguage), first.ReceivedAt, first.AudioGeneration);
                var pair = multilingualResult.For(new(update.SourceLanguage!, update.PrimaryLanguage!,
                    update.SecondaryLanguage!, update.AssignmentEpoch));
                lock (_gate)
                    if (!_disposed && !_lifetime.IsCancellationRequested && update.SpeechBlockRevision == _manualRevision)
                        Caption?.Invoke(update with { Text = pair.Primary, FrenchText = pair.Secondary });
                return;
            }
            if (first.DetectSegment is not null)
            {
                detection = DetectBoundedAsync(first.DetectSegment, text, ageWork.Token);
                var language = await detection.WaitAsync(ageWork.Token);
                await first.EarlierWork.WaitAsync(ageWork.Token);
                update = AssignDetectedSegment(update, language, first.ReceivedAt, first.AudioGeneration);
            }
            else await first.EarlierWork.WaitAsync(ageWork.Token);
            var assigned = new LanguageAssignment(update.SourceLanguage!, update.PrimaryLanguage!, update.SecondaryLanguage!, update.AssignmentEpoch);
            ageWork.Token.ThrowIfCancellationRequested();
            work = _translateAssigned!(text, assigned, ageWork.Token);
            var result = await work.WaitAsync(ageWork.Token);
            lock (_gate)
                if (!_disposed && !_lifetime.IsCancellationRequested && update.SpeechBlockRevision == _manualRevision &&
                    !ageWork.IsCancellationRequested)
                    Caption?.Invoke(update with { Text = result.Primary, FrenchText = result.Secondary });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (work is not null) _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (OperationCanceledException) when (ageWork?.IsCancellationRequested == true)
        {
            if (work is not null) _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            ReportExpiredTranslation(first.Update.SpeechBlockRevision);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                // Natural retirement keeps accepted chunks alive, but retired
                // provider failures cannot stop a different speech block.
                if (!_disposed && !_lifetime.IsCancellationRequested && first.Update.SpeechBlockRevision == _manualRevision)
                {
                    if (error is CaptionSessionException { IsTransientTranslation: true } transient)
                        Diagnostic?.Invoke(transient.Message);
                    else if (Assignment?.Epoch == first.Update.AssignmentEpoch)
                    {
                        Failed?.Invoke(error is CaptionSessionException safe ? safe.Message : "Google text translation failed. Restart the session to continue.");
                        _lifetime.Cancel(); _queue.Writer.TryComplete();
                    }
                    else Diagnostic?.Invoke("Google text translation failed for an earlier speech block; its unfinished caption is unavailable.");
                }
            }
        }
        finally
        {
            ageWork?.Cancel();
            if (detection is not null) ObserveFault(detection);
            if (multilingualWork is not null) ObserveFault(multilingualWork);
            ageWork?.Dispose();
            lock (_gate) { --_pendingSourceCount; first.Completed.TrySetResult(); }
        }
    }

    private async Task<string?> DetectBoundedAsync(Func<string, CancellationToken, Task<string?>> detect,
        string text, CancellationToken token)
    {
        await _detectionSlots.WaitAsync(token);
        try { return await detect(text, token); }
        finally { _detectionSlots.Release(); }
    }

    private async Task<MultilingualCaptionResult> TranslateBoundedAsync(string text, string previousSource,
        string? trustedLanguage, long epoch, CancellationToken token)
    {
        await _translationSlots.WaitAsync(token);
        try
        {
            lock (_gate) if (_knownBlockedSource.Contains(previousSource)) previousSource = "";
            return await _translateMultilingual!(text, previousSource,
                trustedLanguage is "auto" ? null : trustedLanguage, token);
        }
        catch (CaptionSessionException error) when (error.IsContentBlocked)
        {
            lock (_gate)
            {
                var remembered = ShortSource(text);
                if (_knownBlockedSource.Add(remembered)) _blockedSourceHistory.Enqueue(remembered);
                if (_previousAcceptedSource.GetValueOrDefault(epoch) == remembered)
                    _previousAcceptedSource.Remove(epoch);
                if (_blockedSourceHistory.Count > 256) _knownBlockedSource.Remove(_blockedSourceHistory.Dequeue());
            }
            throw;
        }
        finally { _translationSlots.Release(); }
    }

    private static string TrustedOrDetected(string? trusted, string detected)
    {
        if (trusted is not null and not "auto") return trusted;
        return detected is "other" or "und" ? "auto" : detected;
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        faulted => { _ = faulted.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private CancellationTokenSource CreateAgeDeadline(long acceptedAt, CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = _maxTranslationAge - Stopwatch.GetElapsedTime(acceptedAt);
        if (remaining > TimeSpan.Zero) deadline.CancelAfter(remaining);
        else deadline.Cancel();
        return deadline;
    }

    private CaptionUpdate AssignDetectedSegment(CaptionUpdate segment, string? detected, double receivedAt, long audioGeneration)
    {
        var language = SpeechLanguageAssignment.Normalize(detected) ?? "auto";
        lock (_gate)
        {
            var assignment = !_disposed && audioGeneration == AudioGeneration &&
                Assignment?.Epoch == segment.AssignmentEpoch
                ? _assignment.Accept(language, receivedAt, segment.Text.Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries).Length) : null;
            assignment ??= SpeechLanguageAssignment.ForSource(language, segment.AssignmentEpoch);
            return segment with { SourceLanguage = assignment.Input, PrimaryLanguage = assignment.Primary,
                SecondaryLanguage = assignment.Secondary };
        }
    }

    private void ReportExpiredTranslation(long manualRevision)
    {
        lock (_gate) if (!_disposed && !_lifetime.IsCancellationRequested && manualRevision == _manualRevision)
            Diagnostic?.Invoke("Google text translation exceeded the live caption age limit; the delayed segment was skipped. Capture continues.");
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_lifetime.Token))
            {
                if (!_queue.Reader.TryRead(out var first)) continue;
                _lifetime.Token.ThrowIfCancellationRequested();
                BatchStarted?.Invoke(new[] { first.Update });
                var task = ProcessAssignedAsync(first, first.Update.Text.Trim());
                lock (_gate) _scheduled.Add(task);
                _ = task.ContinueWith(finished => { lock (_gate) _scheduled.Remove(finished); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_disposed || _lifetime.IsCancellationRequested) return;
                Failed?.Invoke(error is CaptionSessionException safe ? safe.Message : "Google text translation failed. Restart the session to continue.");
                _lifetime.Cancel();
                _queue.Writer.TryComplete();
            }
        }
        finally
        {
            Task[] scheduled;
            lock (_gate) scheduled = _scheduled.Concat(_receivedFinalWork).ToArray();
            await Task.WhenAll(scheduled);
            lock (_gate) foreach (var work in _normalWork.Values.SelectMany(items => items)) work.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _manualWork?.Cancel(); _manualWork?.Dispose(); _lifetime.Cancel(); _queue.Writer.TryComplete(); }
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _ = Completion.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
