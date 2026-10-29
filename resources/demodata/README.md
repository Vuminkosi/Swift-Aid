# Demo data for local development

`demodata.csv` is the canonical runtime copy of the development demo dataset.
It is embedded into `SwiftAid.Infrastructure` and loaded only when
`DemoData:Enabled` is `true` and the host is not running in Production.

The same content exists as an authoring copy at `designs/data/demodata.csv`
(that folder is git-ignored). If the dataset changes, update both copies and
keep them byte-identical.

Demo rows seed managed cases owned by the dedicated `demo-officer-1` actor in
the `org-demo-1` organization. They are visible only through that actor's
authorized scope and never grant real privileges.
