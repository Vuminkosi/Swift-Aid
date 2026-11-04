# Ellai operational schedule configuration

The quality and follow-up schedulers use `CadenceHours` (4 or 8), `PolicyVersion`, and a positive `ConfigurationRevision`. The default cadence is 4 hours and the default revision is 1. Configuration is scoped by the worker's explicitly configured workspace and organization lists.

Apply the operational schedule configuration revision migration before deploying workers that use it. Deploy the revision-aware worker version to all replicas before increasing a configuration revision: older binaries do not enforce the revision when claiming work. Do not enable new settings until the old workers have drained.

To change a cadence or policy, increase the revision for that role on every replica and apply identical settings. A newer revision waits for any active lease to finish or expire, fences the old lease, and makes the schedule due. A replica with an older revision cannot overwrite or claim the newer schedule. Reusing a revision with different settings produces a mismatch warning and does not run the sweep. A policy version change does not itself prove that every previously processed artifact has been reassessed.

The migration refuses rollback while any schedule has a revision greater than 1. Retain the schedule data and use a forward correction for configuration mistakes.
