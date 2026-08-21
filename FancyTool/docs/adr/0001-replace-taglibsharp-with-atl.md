# Replace TagLibSharp with ATL (z440.atl.core) for music title extraction

The folder-compare tool extracts the embedded `Title` tag of audio files to support music-title comparison. This was done with TagLibSharp, an LGPL-licensed library whose format factories rely on reflection — under the project's Native AOT publish it required a `<TrimmerRootAssembly>` root and still carried trimming risk. We replaced it with ATL (`z440.atl.core`): a fully managed, MIT-licensed library whose format detection and reader selection are static switch expressions, so it trims cleanly under AOT without a root assembly and without reflection warnings.

**Status**: accepted

**Considered Options**:
- **Keep TagLibSharp + `TrimmerRootAssembly`** — worked, but LGPL license and a reflection-based factory that must be fully rooted under AOT.
- **ATL (`z440.atl.core`)** — chosen: MIT license, no runtime dependencies, static (non-reflection) factories, covers all 10 audio extensions the tool recognizes (incl. `.ape`).

**Consequences**:
- ATL does not throw on corrupt or unsupported files; `ExtractMusicTitle` relies on a single defensive catch and falls back to the file name.
- `ATL.Track` is not `IDisposable`, so the previous `using` statement was removed.
- ATL targets `net6.0` and does not declare `IsAotCompatible`; verified the Release AOT publish produces a working native executable.
