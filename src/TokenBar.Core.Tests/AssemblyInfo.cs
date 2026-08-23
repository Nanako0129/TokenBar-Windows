// Localization.Load installs one process-wide table, so a test that loads
// zh-Hant races every other class asserting English copy — and xUnit's default
// is to run collections in parallel. Serializing the assembly is one line
// against a whole class of flake that would otherwise have to be remembered
// for every future test touching a translated surface.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
