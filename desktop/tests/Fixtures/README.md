# Dictionary regression fixture

`digital.v5.json` is the complete, unmodified semantic payload of `entries.document_json`
for English `digital`, extracted from the installed Open Dictionary SQLite artifact
(84,212 entries, `distribution_entry_v5` / `distribution_sqlite_v1`). JSON whitespace
was reformatted. No private vocabulary or user settings are included.

Source: https://github.com/ahpxex/open-dictionary
Contract: https://github.com/ahpxex/open-dictionary/blob/main/docs/export_contracts.md

Dictionary data attribution: Open Dictionary, English Wiktionary and its contributors,
and Wiktextract. Data license: CC BY-SA 4.0, https://creativecommons.org/licenses/by-sa/4.0/.
The fixture remains under this data license independently of the application code.

This regression covers the original ordering (the finger sense precedes core technology
senses), both parts of speech, and two rare noun senses that must remain expandable.
