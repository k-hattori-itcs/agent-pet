# Contributing

1. Create a feature branch from `main`.
2. Keep changes focused and do not commit local `pet_data` configuration, logs, transcripts, build output, credentials, or absolute local paths.
3. Run `dotnet restore AgentPet.sln`, `dotnet format AgentPet.sln`, `dotnet build AgentPet.sln -c Release`, and `dotnet test AgentPet.Tests/AgentPet.Tests.csproj -c Release`.
4. Describe behavior changes, security impact, and manual verification in the pull request.
5. Use Conventional Commit messages such as `fix: reject unsafe pet package paths`.

Security reports must follow [SECURITY.md](SECURITY.md), not public issues.

CIは全体行カバレッジ15%を現在の最低基準として強制します。セキュリティ境界を変更する場合は、変更箇所の正常系と拒否系テストを追加し、基準値を下げないでください。
