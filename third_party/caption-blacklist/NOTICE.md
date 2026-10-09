# English caption blacklist attribution

CapFast's `src/CapFast/Core/EnglishCaptionBlacklist.txt` is adapted from these sources, retrieved 2026-10-09:

- Robert James Gabriel / Coffee & Fun LLC, `google-profanity-words`, English `data/en.txt`, commit `0ae3460863120bc671361b9403cc65d5f2075b89`, MIT. [Pinned source](https://github.com/coffee-and-fun/google-profanity-words/blob/0ae3460863120bc671361b9403cc65d5f2075b89/data/en.txt). Full license: `coffee-and-fun-MIT.txt`.
- © 2012–2020 Shutterstock, Inc., LDNOOBW, English `en`, commit `5faf2ba42d7b1c0977169ec3611df25a3c08eb13`, Creative Commons Attribution 4.0 International. [Pinned source](https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words/blob/5faf2ba42d7b1c0977169ec3611df25a3c08eb13/en). Full license: `LDNOOBW-CC-BY-4.0.txt`; [license](https://creativecommons.org/licenses/by/4.0/).

Changes: whitespace trimming, lowercase conversion, deduplication, union of the two lists; removal of ordinary generic words, neutral identities, historical/political terms and ambiguous nonsexual phrases; explicit common inflections, censored spellings, expletive abbreviations, sexual anatomy/activity terms and euphemistic phrases added by CapFast. No endorsement by the upstream authors is implied. The list is intended for a deliberately conservative seminar display and is not a categorization of people or identities.

Removed upstream entries (other explicit phrases containing these words may remain):

`big black`, `blonde action`, `blonde on blonde action`, `brunette action`, `bullet vibe`, `cox`, `dike`, `dink`, `dinks`, `domination`, `dumb`, `escort`, `flamer`, `flange`, `fool`, `girl on`, `gringo`, `guido`, `hard core`, `hardcore`, `hot chick`, `how to kill`, `how to murder`, `huge fat`, `jelly donut`, `jerk`, `knob`, `leather restraint`, `leather straight jacket`, `lolita`, `masochist`, `nazi`, `neonazi`, `pawn`, `playboy`, `pollock`, `sadism`, `sadist`, `santorum`, `scat`, `shrimping`, `snowballing`, `suck`, `sucker`, `sucks`, `swastika`, `swinger`, `tainted love`, `taste my`, `tied up`, `tight white`, `tongue in a`, `twinkie`, `wrapping men`, `xx`.

The combined modified data retains the upstream MIT and CC-BY-4.0 notices; this does not relicense the rest of CapFast. Keep this notice and both licenses with redistributed copies of the list. The app project packages these notices with its output.

Update procedure: review the pinned upstream diffs rather than replacing this list blindly; preserve intentional exclusions, add explicit variants, update the commit links and counts, and run the content-filter checks. Slang changes and no fixed list can cover every sexual implication or invented spelling.
