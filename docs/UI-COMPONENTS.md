# UI component decision register

Approved owner decision (2026-10-26): **MudBlazor is primary** for ordinary workflows and the shared visual system. **Syncfusion is reserved for justified complex specialized components** where a concrete requirement benefits from it. Both must follow one SwiftAid token system; MudBlazor owns shared visual identity. This does not change product requirements.

| Surface / use | Provider | Decision / gate | Package and license evidence | Android/web result |
|---|---|---|---|---|
| App shell, navigation, forms, validation, dialogs, notifications, cards, tabs, lists, standard tables, responsive and accessibility states | MudBlazor | Primary/default | Directory.Packages.props declares MudBlazor 9.11.0 in the observed scaffold; restore compatibility and license/package review not verified. | NOT VERIFIED |
| Advanced case queue grid, multi-resource scheduler, complex interactive analytics, map/geospatial visualization, or other specialist control | Syncfusion, only if requirement warrants | Conditional; do not use for ordinary controls or duplicate a MudBlazor control without a recorded decision. | No package/license evidence observed. Verify supported package versions, license eligibility/key protection before enabling. | NOT VERIFIED |

Do not treat this register as proof a control is implemented. Before a Syncfusion use, record screen, exact component, reason MudBlazor is insufficient, package version, license basis, Android and web tests, accessibility, styling, offline and performance constraints. Keep a functional MudBlazor fallback while license/compatibility is unresolved.
