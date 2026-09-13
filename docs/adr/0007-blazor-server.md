# ADR-0007 — Blazor Web App with interactive server rendering

**Status:** accepted

## Decision

Blazor with `InteractiveServerRenderMode`, no JavaScript framework, hand-written CSS.

## Reasoning

The specification requires Blazor (§2). Server rendering in particular suits a financial review tool:
sensitive data and authorisation decisions stay on the server, and the UI can call application services
directly rather than through a second API surface that would need its own authorisation.

CSS is hand-written rather than pulled from a component library because a review screen needs density,
unambiguous status and high contrast far more than it needs a design system — and because every status
must be conveyed by text and an icon as well as colour (§15), which is easier to guarantee when you
control the markup.

## Given up

Offline capability and the need for a persistent connection per user. Neither matters for an internal
review tool. WebAssembly would cost a second authorisation surface and put data in the browser.
