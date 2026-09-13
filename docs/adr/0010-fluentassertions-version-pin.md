# ADR-0010 — FluentAssertions pinned to 7.2.2

**Status:** accepted

## Decision

Pin FluentAssertions to 7.2.2 rather than taking the latest 8.x.

## Reasoning

The specification names FluentAssertions (§2). Version 8 moved to the Xceed commercial licence, which
requires a paid per-developer licence for commercial use. Taking the latest version would silently
impose a licensing cost and a compliance question on anyone who builds this repository.

7.2.2 is the last Apache-2.0 release, is feature-complete for this test suite, and carries no such
obligation.

This is flagged rather than buried because a dependency that quietly changes a project's licensing
position is exactly the sort of thing that should be an explicit decision.

## Given up

Improvements in 8.x. If the licence is acquired, changing the pin is a one-line edit in
`Directory.Packages.props`.

An alternative, if the licence is unacceptable long-term, is migrating to Shouldly or to xUnit's own
assertions.
