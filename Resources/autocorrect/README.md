# Autocorrect lists

`wikipedia-common-misspellings.txt` is the machine-readable version of
Wikipedia's [Lists of common misspellings](https://en.wikipedia.org/wiki/Wikipedia:Lists_of_common_misspellings/For_machines)
(revision 1199637275, 27 January 2024), by Wikipedia contributors, licensed
under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). Only the
header comment and whitespace were changed. This file stays under CC BY-SA 4.0;
the rest of OpenTypist is MIT.

Entries with one correction are fixed instantly. Entries with several options
(`achive->achieve, archive`), and entries whose misspelling is also a real word
in your languages (`loosing`, `payed`), are left to the language model, which
picks the most likely word from the surrounding text.

You can add your own lists in Settings → General → Autocorrect → Import List…:
plain `misspelling->correction` files, or AutoHotkey hotstring scripts such as
[AutoCorrect.ahk](https://github.com/oxinabox/AutoCorrect). Imported lists are
copied to `~/Library/Application Support/OpenTypist/Autocorrect/` and are not
part of this repository.
