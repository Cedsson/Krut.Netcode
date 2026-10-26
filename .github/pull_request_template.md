## Summary

<!-- What changes and why. Link the issue if there is one. -->

## Checklist

- [ ] Tests added or updated, and `dotnet test` passes
- [ ] `dotnet build -c Release -warnaserror` has no warnings
- [ ] Native AOT still publishes without trim or AOT warnings (CI `aot` job)
- [ ] Hot paths stay allocation-free (the allocation tests pass)
- [ ] Public API has XML documentation; docs updated if behavior changed
- [ ] `CHANGELOG.md` updated under "Unreleased"