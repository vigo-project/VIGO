## What and why

## Checklist

- [ ] Build and tests pass (`dotnet build VIGO.slnx -p:Platform=x64`, `dotnet test --project tests/...`)
- [ ] New user-facing text is in both `.resw` files (fr-FR and en-US)
- [ ] A line was added to `CHANGELOG-en.md` and `CHANGELOG-fr.md` (user-visible changes)
- [ ] No new Native AOT warning (`/aot-check`), if the change touches XAML, reflection or serialization
- [ ] New dependencies are permissive and listed in `THIRD-PARTY-NOTICES.md`
