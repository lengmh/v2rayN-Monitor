# Third-party notices

The original v2rayN Monitor code is MIT licensed. It uses these separately licensed components, without copying v2rayN or Xray implementation source:

| Component | License | Source |
|---|---|---|
| .NET / ASP.NET Core / Windows Forms / Microsoft.Data.Sqlite / System.Management | MIT | https://github.com/dotnet |
| SQLitePCLRaw | Apache-2.0, Copyright 2014–2025 SourceGear, LLC | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain | https://sqlite.org/copyright.html |
| Vite (build tool) | MIT | https://github.com/vitejs/vite |
| TypeScript (build tool) | Apache-2.0 | https://github.com/microsoft/TypeScript |
| Playwright (test tool) | Apache-2.0 | https://github.com/microsoft/playwright |
| xUnit (test tool) | Apache-2.0 | https://github.com/xunit/xunit |

License texts for distributed runtime components are in `licenses/` and included in the portable archive. Package versions are specified in project files and `web/package-lock.json`. Frontend code is original vanilla TypeScript; build/test tools are not installed on end-user machines.
