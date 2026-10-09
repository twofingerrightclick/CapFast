using CapFast.Core;

namespace CapFast.Speech;

public interface IAudioActivitySession
{
    event Action<AudioActivity>? AudioActivityChanged;
}
