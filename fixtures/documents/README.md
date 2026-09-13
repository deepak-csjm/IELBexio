# Document fixtures

## `facturx-invoice.pdf`

A minimal Factur-X / ZUGFeRD invoice: a PDF whose catalogue carries an `/EmbeddedFiles` name tree
pointing at `factur-x.xml`, which holds the invoice as UN/CEFACT Cross Industry Invoice XML. Two lines
at 8.1% Swiss VAT, totalling 1,081.00 CHF.

Upload it at **Documents** to see the deterministic PDF path: the invoice is read from the embedded
XML exactly, with no character recognition and no language model involved.

It is synthetic — an invented company and an invented invoice number — because §25 forbids real
financial data in fixtures. It is deliberately minimal rather than a full PDF/A-3 file: it exercises
the structure the reader depends on and nothing else.

`PdfFixtures.WithAttachment` in `tests/IelBexio.UnitTests/Support` builds the same structure and is the
authority on it; this file was produced with that code so the fixture and the tests cannot drift apart
in what they claim a Factur-X document looks like.

**Verification status: unverified against a document produced by commercial invoicing software.** See
`CrossIndustryInvoiceParser` for what that means for the element names.

## `printed-invoice.pdf`

An ordinary printed invoice: a text layer and no structured data. Upload it to see the other half of
the PDF path — the document is stored, hashed, classified and then **refused**, with a message saying
how many pages and characters were found. It is never partially extracted, because an invoice that
looks finished and is not is worse than one that was obviously not read.
