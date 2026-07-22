# UsbCopyService

Web service that runs near the backup storage (FTP). On client request (UsbCopyConsole) it downloads files
from the storage to its local disk, optimizes them for long-distance transfer (small files are combined
into zip archives, large files are split into byte-range parts) and pushes ready packages to the client
over a persistent SignalR connection; package bytes are streamed over HTTP from the same port.

## Project references

This solution does not clone its own copies of the shared libraries. It references existing clones:

- `D:\1WorkDotnet\UsbCopy\{SystemTools, ToolsManagement, ParametersManagement, ConnectionTools, WebSystemTools}`
- Shared contracts: `..\UsbCopyServiceShared\UsbCopyServiceShared.Contracts` (also referenced by UsbCopyConsole)

If sibling clones are later placed inside this folder (Crawler-style layout), only the relative paths in
`UsbCopyService.slnx` and `UsbCopyService\UsbCopyService.csproj` need to be updated.

## Build and run

```powershell
dotnet build UsbCopyService.slnx

# interactive run (as a Windows Service it starts without --console)
dotnet run --project UsbCopyService -- --console
```

## Configuration (appsettings.json)

- `Kestrel:Endpoints:Http:Url` — listen address, e.g. `http://*:5099`
- `ApiKeys:AppSettingsByApiKey` — list of `{ ApiKey, RemoteIpAddress }` pairs; a client is authorized only
  if both the `?apikey=` query value and its source IP match one pair. Note: IPv6 `::1` does not match
  `127.0.0.1` — use `http://127.0.0.1:5099` (not `localhost`) for local tests.
- `Serilog` — standard Serilog configuration section
- `UsbCopySettings`:
  - `WorkPath` — working directory for temporary downloads/archives (job_* subfolders)
  - `SmallFileMaxSizeMb` (4) — files up to this size are packed into zip archives
  - `ArchiveVolumeMaxSizeMb` (200) — max total original size of one zip volume
  - `PartMaxSizeMb` (256) — larger files are split into parts of this size
  - `AckTimeoutMinutes` (60), `CleanWorkPathOnStart` (true)
  - `FileStorages`, `ExcludeSets`, `Projects` — same models as UsbCopy parameters file

## Endpoints

- SignalR hub `api/v1/usbcopy/hub` (authorized): client calls `StartJob(projectName, existingFiles)` /
  `AckPackage(jobId, packageId, ok, error)`; service pushes `ReceiveProgress`, `ReceivePackageReady(manifest)`,
  `ReceiveJobCompleted(summary)`, `ReceiveJobFailed(error)`.
- `GET api/v1/usbcopy/download/{jobId}/{packageId}` (authorized) — raw stream of the currently offered package.
- `GET api/v1/test/getversion` and other test endpoints (anonymous) — used by ApiClient validation.
