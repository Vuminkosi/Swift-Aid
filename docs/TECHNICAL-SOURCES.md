# Technical source register

This index points to the source material used to plan technical work. Listing a source is not evidence that its API/package version was verified in this checkout. The current compatibility task is P0.1/P0.2; use the exact source editions identified in `implementation.md` §24 and the requirement references in `spec.md`.

| Topic | Authoritative source location | Current verification |
|---|---|---|
| .NET SDK, MAUI and Blazor Hybrid | `implementation.md` §24.1 entries T01–T07 | Sources inherited from the plan; current environment/build compatibility NOT VERIFIED. |
| EF Core, Npgsql, PostGIS and identity | `implementation.md` §24.1 entries T08–T09 and §24.5 | Implementation references; no migration or live service evidence. |
| Microsoft AI, MCP and Meta APIs | `implementation.md` §24.1 entries T10–T12P and §24.2 | Account, model, ASR, encoder, price and endpoint capabilities NOT VERIFIED. |
| NATS, media and uploads | `implementation.md` §24.1 entries T13–T18 | Package/API compatibility NOT VERIFIED. |
| Datasync | `implementation.md` §24.1 entries T03–T06 and §24.5 | Android Release proof pending P0.2. |
| Qdrant | `implementation.md` §24.5 and §5.2a; product requirements in `spec.md` §15.7 | Scoped service, payload policy, invalidation and outage behavior NOT VERIFIED. |
| UI libraries | `implementation.md` §5 and attached owner UI decision | MudBlazor selected primary; Syncfusion conditional. Package/license/platform behavior NOT VERIFIED. |
| Health/veterinary content sources | `implementation.md` §§13, 24.2–24.4; `spec.md` Appendix A | Rights, edition, clinical/veterinary review and permitted use are source-specific and unresolved until recorded. |

When a source changes, update the version/edition and test evidence in the relevant task record; do not infer current compatibility from a plan citation.
