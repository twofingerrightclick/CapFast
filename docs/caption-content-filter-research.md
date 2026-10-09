# English caption omission filter research

Research date: 2026-10-09. The requested policy is broad and conservative: remove blacklisted English profanity, expletives, sexual or suggestive words/phrases, vulgarities and slurs before captions become visible, including the word “sex”. Remove matches completely, without asterisks, dots or replacement text. This research supports a deterministic local display filter, with no additional Google requests.

## Sources selected

| Source | Evidence and license | Use |
| --- | --- | --- |
| Coffee & Fun `google-profanity-words` | The owner provides English data and a MIT license. This is a community-maintained project; its name does not make it an official Google service or Google policy. | Broad base covering profanity, common spelling variants, sexual vocabulary and slurs. |
| Shutterstock / LDNOOBW | The owner describes a list originally used for autocomplete/recommendation filtering, explicitly notes that classification is subjective, and licenses the work under CC-BY-4.0. | Additional sexual terms, phrases and variants missing from the first list. |

Primary sources: [Coffee & Fun English data](https://github.com/coffee-and-fun/google-profanity-words/blob/0ae3460863120bc671361b9403cc65d5f2075b89/data/en.txt), [Coffee & Fun license](https://github.com/coffee-and-fun/google-profanity-words/blob/0ae3460863120bc671361b9403cc65d5f2075b89/LICENSE), [LDNOOBW owner README](https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words/blob/5faf2ba42d7b1c0977169ec3611df25a3c08eb13/README.md), [LDNOOBW English data](https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words/blob/5faf2ba42d7b1c0977169ec3611df25a3c08eb13/en), [LDNOOBW license](https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words/blob/5faf2ba42d7b1c0977169ec3611df25a3c08eb13/LICENSE).

The local resource contains **1,314 unique words or phrases**, including **164 multiword entries**. The normalized upstream union contained 969 entries; 55 unrelated/generic or neutral entries were removed, and 400 additional distinct entries were added. All entries are reviewable in [EnglishCaptionBlacklist.txt](../src/CapFast/Core/EnglishCaptionBlacklist.txt). The upstream commit IDs, full licenses and exact removal audit are retained under [third_party/caption-blacklist](../third_party/caption-blacklist/NOTICE.md).

## Recommended matching behavior

These are CapFast implementation recommendations rather than claims that an upstream library provides this policy:

1. Run the local filter at the common caption ingress, before any buffer, queue, animation or screen text receives provider output. Cover both interim and final events from every supported provider. Do not rely solely on a translation prompt or cloud safety settings.
2. Match full lexical tokens case-insensitively, normalize common Unicode/apostrophe forms for comparison, preserve the untouched surrounding text, and add explicit common inflections. Avoid global substring deletion: ordinary words such as “class”, “assessment”, “assistant”, “shell”, “Sussex” and “Scunthorpe” should survive.
3. Support complete multiword phrases across spaces and hyphens. Do not combine ordinary phrases across sentence-ending punctuation. Explicit censored spellings and dotted abbreviations need literal matching; punctuation stripping must not turn an innocent phrase into a different match. Match the longest available phrase when entries overlap.
4. Remove the whole matched word/phrase with no placeholder, then collapse leftover whitespace and tidy adjoining punctuation. If the entire event is removed, replace any previous interim text for that utterance with an empty event so unsafe revisions cannot remain visible.
5. If interim output can arrive split inside a phrase or censored token, keep a small pending suffix or sanitize the cumulative utterance before presenting it. A standalone safe prefix of a later blocked phrase should not bypass omission simply because it arrived earlier.
6. Keep a plain text resource with one literal word/phrase per line and comment lines beginning `#`. Ship source attribution/license files with app output; update through reviewed upstream diffs and targeted local checks.

## Coverage and deliberate tradeoffs

The list covers common swear-word families and compound forms, mild expletives, abbreviated/censored forms, anatomical and sexual vocabulary, pornography, sexual acts, exploitative sexual terminology, explicit euphemisms and commonly encountered slurs. Both British and American profanity are represented. Terms such as “sex”, “sexual”, “sexually”, “sexy”, “breast”, “cock”, “balls”, “hell” and “damn” are intentionally omitted even when a context might be innocent: this follows the requested conservative display policy.

The list does not globally omit sexual-orientation identity words. Words such as “gay” and “lesbian” are not globally omitted; explicit sexual/slur phrases can still be matched. Ordinary upstream entries such as “fool”, “dumb”, “jerk”, “knob”, “flange”, “escort”, “suck”, “tied up”, “jelly donut”, “pawn”, “Nazi” and “swastika” were excluded because they are not inherently profanity or sexual content. Contextually innocent words with common sexual meanings, such as “pegging”, “scissoring”, “screwing”, “snatch”, “willy”, “wang”, “twink” and “undressing”, are retained under the requested broad policy. Those choices are a local content-policy judgment, documented in the removal audit, and can be adjusted later.

A blacklist cannot be literally exhaustive: sexual implication can be expressed using entirely ordinary words, and invented spellings or language mixing can evade any fixed vocabulary. Removing anatomical/sexual terms will also shorten legitimate medical, family or religious discussions. No semantic classifier or additional cloud moderation is added here because the user requested predictable word omission and low token usage. These limits should be stated without claiming that a fixed list guarantees an accident-free screen.

## Local validation recommended

Implementation boundary: providers' cumulative interim updates are sanitized in full, and every individual blacklisted word is removed before delivery. Multiword matching is confined to each caption update; a phrase split across independently finalized translation chunks is not joined for moderation. No extra cross-chunk buffering or display delay is introduced. Recommendations above describe possible extensions rather than a claim that this implementation moderates semantic meaning or every cross-chunk phrase.

Exercise representative profanity, anatomical words, common inflections, phrase separators, Unicode/full-width spellings, possessives and censored forms; confirm safe substring examples survive. Verify all-blacklisted interim/final revisions clear prior text. Check phrase prefixes across incremental events and punctuation boundaries. Verify the embedded resource is packaged and attribution/licenses are copied into release output. Use deterministic local tests rather than paid speech/translation requests for this change.

## Verification record — October 9, 2026

Complete executable checks passed against the final 1,314-entry resource, and the app build passed with zero warnings/errors. Tests remove every listed entry in original case, uppercase, and surrounded by safe caption text; exercise exact boundaries, phrases, hyphens, censored forms, fullwidth/supplementary Unicode compatibility forms, possessives, invisible format characters, suffix ordering and quote/punctuation cleanup; and process a large local input to detect severe performance regressions. Controller/presentation checks verify that only filtered text reaches history and display frames, source identity/finality remain intact, an entirely filtered interim retracts its active tail without prematurely sealing the source, later clean revisions still appear, and filtered final events retain duplicate suppression. Independent review found no outstanding implementation issues. No cloud requests were used for this slice.

The Apple Silicon bundle was republished and passed strict signature and property-list verification. The packaged output includes `third_party/caption-blacklist/NOTICE.md` and both license texts. The updated app opened empty and paused with BlackHole selected.
