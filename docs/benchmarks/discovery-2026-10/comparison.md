
### cold — n = 6 legacy / 6 optimized

| Metric | Legacy median | Optimized median | Δ | Δ % | range (legacy / optimized) | p | verdict |
|---|---:|---:|---:|---:|---|---:|---|
| Total population wall time (ms) | 47,875 | 46,512 | −1,363 | -2.8% | 47,353–48,660 / 45,057–47,474 | 0.013 | within noise |
| Process CPU time (ms) | 40,438 | 39,445 | −992 | -2.5% | 33,438–42,234 / 30,266–45,922 | 0.987 | within noise |
| Managed allocations (all threads) (MB) | 71,934.7 | 70,700.7 | −1,234.0 | -1.7% | 71,725.2–72,066.3 / 70,647.7–70,947.2 | 0.013 | clear |
| Peak working set (process) (MB) | 3,332.9 | 2,878.8 | −454.1 | -13.6% | 3,207.8–3,376.2 / 2,803.1–3,021.2 | 0.013 | clear |
| FaceGen path cache (total) (ms) | 2,067 | 424 | −1,643 | -79.5% | 1,905–2,351 / 406–446 | 0.013 | clear |
|   preliminary entry discovery (ms) | 1,072 | 17 | −1,055 | -98.4% | 1,047–1,392 / 17–18 | 0.013 | clear |
|   loose FaceGen discovery (ms) | 356 | 180 | −176 | -49.4% | 348–368 / 170–185 | 0.013 | clear |
|   archive association (plugin -> BSA) (ms) | 236 | 190 | −45 | -19.1% | 196–245 / 185–210 | 0.013 | within noise |
|   archive FaceGen indexing (ms) | 371 | 31 | −341 | -91.7% | 259–425 / 30–32 | 0.013 | clear |
| Mod-folder scan (ms) | 34,219 | 34,045 | −174 | -0.5% | 33,655–34,745 / 33,514–35,342 | 0.688 | within noise |
| Analysis (RefreshNpcLists etc.) (ms) | 7,695 | 7,738 | +43 | +0.6% | 7,303–8,243 / 7,315–8,507 | 0.844 | within noise |

| Logical counter | Legacy | Optimized |
|---|---:|---:|
| LooseFoldersScanned | 717 | 717 |
| LooseRootsEnumerated | 1,113 | 1,020 |
| LooseCandidatePaths | 162,385 | 84,353 |
| LoosePathsRetained | 162,385 | 84,353 |
| PreliminaryCandidates | 719 | 719 |
| TemporaryViewModelsConstructed | 719 | 0 |
| ArchivesConsidered | 335 | 335 |
| ArchivesProbed | 0 | 335 |
| ArchivesProbeAbsent | 0 | 146 |
| ArchivesProbePresent | 0 | 189 |
| ArchivesFullyIndexed | 335 | 0 |
| ArchiveFoldersRead | 0 | 391 |
| ArchiveFileEntriesEnumerated | 567,541 | 40,126 |
| ArchivePathsRetained | 40,126 | 40,126 |
| PluginDirectoryListings | 4,587 | 4,587 |
| PluginLoads | 26,292/26,295/26,296/26,298/26,304/26,306 | 26,287/26,292/26,294/26,295/26,297/26,301 |

### warm — n = 6 legacy / 6 optimized

| Metric | Legacy median | Optimized median | Δ | Δ % | range (legacy / optimized) | p | verdict |
|---|---:|---:|---:|---:|---|---:|---|
| Total population wall time (ms) | 12,627 | 10,921 | −1,706 | -13.5% | 12,330–13,234 / 10,696–12,464 | 0.013 | within noise |
| Process CPU time (ms) | 9,188 | 8,289 | −898 | -9.8% | 8,312–9,703 / 7,312–8,969 | 0.019 | within noise |
| Managed allocations (all threads) (MB) | 13,137.4 | 12,135.1 | −1,002.2 | -7.6% | 13,130.2–13,147.8 / 12,114.3–12,243.4 | 0.013 | clear |
| Peak working set (process) (MB) | 2,183.3 | 1,827.0 | −356.3 | -16.3% | 2,155.0–2,209.5 / 1,777.8–1,879.1 | 0.013 | clear |
| FaceGen path cache (total) (ms) | 2,013 | 464 | −1,549 | -77.0% | 1,942–2,180 / 416–554 | 0.013 | clear |
|   preliminary entry discovery (ms) | 1,097 | 17 | −1,080 | -98.4% | 1,066–1,311 / 17–20 | 0.013 | clear |
|   loose FaceGen discovery (ms) | 374 | 200 | −174 | -46.6% | 366–389 / 177–240 | 0.013 | clear |
|   archive association (plugin -> BSA) (ms) | 205 | 197 | −8 | -3.9% | 194–209 / 187–260 | 0.316 | within noise |
|   archive FaceGen indexing (ms) | 292 | 34 | −258 | -88.4% | 264–433 / 30–54 | 0.013 | clear |
| Mod-folder scan (ms) | 144 | 215 | +71 | +49.0% | 127–194 / 174–255 | 0.013 | within noise |
| Analysis (RefreshNpcLists etc.) (ms) | 6,471 | 6,234 | −237 | -3.7% | 6,207–6,728 / 6,150–7,370 | 0.294 | within noise |

| Logical counter | Legacy | Optimized |
|---|---:|---:|
| LooseFoldersScanned | 717 | 717 |
| LooseRootsEnumerated | 1,113 | 1,020 |
| LooseCandidatePaths | 162,385 | 84,353 |
| LoosePathsRetained | 162,385 | 84,353 |
| PreliminaryCandidates | 719 | 719 |
| TemporaryViewModelsConstructed | 719 | 0 |
| ArchivesConsidered | 335 | 335 |
| ArchivesProbed | 0 | 335 |
| ArchivesProbeAbsent | 0 | 146 |
| ArchivesProbePresent | 0 | 189 |
| ArchivesFullyIndexed | 335 | 0 |
| ArchiveFoldersRead | 0 | 391 |
| ArchiveFileEntriesEnumerated | 567,541 | 40,126 |
| ArchivePathsRetained | 40,126 | 40,126 |
| PluginDirectoryListings | 1,334 | 1,334 |
| PluginLoads | 1,251/1,253/1,254/1,257/1,260/1,262 | 1,252/1,255/1,256/1,262/1,264 |

### refresh-all — n = 6 legacy / 6 optimized

| Metric | Legacy median | Optimized median | Δ | Δ % | range (legacy / optimized) | p | verdict |
|---|---:|---:|---:|---:|---|---:|---|
| Total population wall time (ms) | 46,638 | 44,273 | −2,365 | -5.1% | 45,501–48,524 / 43,279–45,604 | 0.013 | within noise |
| Process CPU time (ms) | 31,828 | 30,523 | −1,305 | -4.1% | 29,078–34,062 / 28,188–34,234 | 0.251 | within noise |
| Managed allocations (all threads) (MB) | 71,452.9 | 69,522.5 | −1,930.4 | -2.7% | 71,317.5–71,562.3 / 69,502.3–69,569.8 | 0.013 | clear |
| Peak working set (process) (MB) | 4,411.4 | 3,758.7 | −652.7 | -14.8% | 4,334.0–4,478.1 / 3,670.1–3,819.4 | 0.013 | clear |
| FaceGen path cache (total) (ms) | 1,577 | 364 | −1,213 | -76.9% | 1,496–1,843 / 347–394 | 0.013 | clear |
|   preliminary entry discovery (ms) | 730 | 12 | −719 | -98.4% | 673–809 / 11–12 | 0.013 | clear |
|   loose FaceGen discovery (ms) | 361 | 177 | −184 | -51.0% | 306–460 / 172–223 | 0.013 | clear |
|   archive association (plugin -> BSA) (ms) | 227 | 165 | −62 | -27.3% | 198–239 / 143–184 | 0.013 | clear |
|   archive FaceGen indexing (ms) | 253 | 1 | −252 | -99.7% | 236–384 / 1–1 | 0.013 | clear |
| Mod-folder scan (ms) | 33,786 | 32,950 | −836 | -2.5% | 33,094–34,773 / 32,029–33,885 | 0.082 | within noise |
| Analysis (RefreshNpcLists etc.) (ms) | 6,791 | 6,735 | −56 | -0.8% | 6,310–7,308 / 6,593–7,032 | 0.682 | within noise |

| Logical counter | Legacy | Optimized |
|---|---:|---:|
| LooseFoldersScanned | 717 | 717 |
| LooseRootsEnumerated | 1,113 | 1,020 |
| LooseCandidatePaths | 162,385 | 84,353 |
| LoosePathsRetained | 162,385 | 84,353 |
| PreliminaryCandidates | 719 | 719 |
| TemporaryViewModelsConstructed | 719 | 0 |
| ArchivesConsidered | 335 | 335 |
| ArchivesFullyIndexed | 335 | 0 |
| ArchiveFileEntriesEnumerated | 567,541 | 0 |
| ArchivePathsRetained | 40,126 | 0 |
| ArchiveListingCacheHits | 0 | 335 |
| PluginDirectoryListings | 4,490 | 4,490 |
| PluginLoads | 26,283/26,287/26,290/26,291/26,295 | 26,268/26,277/26,278/26,284/26,292 |

### refresh-mod (changed) — n = 5 legacy / 5 optimized

| Metric | Legacy median | Optimized median | Δ | Δ % | range (legacy / optimized) | p | verdict |
|---|---:|---:|---:|---:|---|---:|---|
| Total population wall time (ms) | 176 | 175 | −1 | -0.6% | 173–208 / 166–184 | 1.000 | within noise |
| Process CPU time (ms) | 125 | 109 | −16 | -12.5% | 94–141 / 94–156 | 1.000 | within noise |
| Managed allocations (all threads) (MB) | 101.6 | 101.5 | −0.0 | -0.0% | 101.5–101.6 / 101.5–101.6 | 0.119 | within noise |
| Peak working set (process) (MB) | 1,752.5 | 1,772.7 | +20.2 | +1.2% | 1,717.3–1,766.7 / 1,748.1–1,811.1 | 0.119 | within noise |
| FaceGen path cache (total) (ms) | 7 | 7 | −0 | -1.3% | 6–10 / 6–7 | 0.714 | within noise |
|   preliminary entry discovery (ms) | 0 | 0 | +0 | +5381.8% | 0–0 / 0–0 | 0.048 | clear |
|   loose FaceGen discovery (ms) | 0 | 0 | −0 | -17.2% | 0–0 / 0–0 | 0.048 | clear |
|   archive association (plugin -> BSA) (ms) | 2 | 1 | −1 | -31.9% | 2–3 / 1–1 | 0.048 | clear |
|   archive FaceGen indexing (ms) | 4 | 4 | +0 | +5.4% | 3–5 / 3–5 | 0.381 | within noise |

| Logical counter | Legacy | Optimized |
|---|---:|---:|
| LooseFoldersScanned | 1 | 1 |
| LooseRootsEnumerated | 2 | 2 |
| LooseCandidatePaths | 8 | 8 |
| LoosePathsRetained | 8 | 8 |
| PreliminaryCandidates | 1 | 1 |
| ArchivesConsidered | 1 | 1 |
| ArchivesProbed | 0 | 1 |
| ArchivesProbePresent | 0 | 1 |
| ArchivesFullyIndexed | 1 | 0 |
| ArchiveFoldersRead | 0 | 2 |
| ArchiveFileEntriesEnumerated | 28 | 28 |
| ArchivePathsRetained | 28 | 28 |
| PluginDirectoryListings | 2 | 2 |
| PluginLoads | 5 | 5 |

### refresh-mod (unchanged) — n = 5 legacy / 5 optimized

| Metric | Legacy median | Optimized median | Δ | Δ % | range (legacy / optimized) | p | verdict |
|---|---:|---:|---:|---:|---|---:|---|
| Total population wall time (ms) | 207 | 195 | −12 | -5.6% | 195–210 / 186–197 | 0.119 | within noise |
| Process CPU time (ms) | 156 | 141 | −16 | -10.0% | 141–234 / 94–219 | 1.000 | within noise |
| Managed allocations (all threads) (MB) | 101.5 | 101.5 | −0.1 | -0.1% | 101.5–101.6 / 101.4–101.6 | 0.381 | within noise |
| Peak working set (process) (MB) | 1,742.7 | 1,771.4 | +28.7 | +1.6% | 1,717.5–1,834.6 / 1,663.0–1,805.2 | 0.381 | within noise |
| FaceGen path cache (total) (ms) | 3 | 3 | −0 | -6.7% | 3–4 / 3–3 | 0.381 | within noise |
|   preliminary entry discovery (ms) | 0 | 0 | +0 | +7033.3% | 0–0 / 0–0 | 0.048 | clear |
|   loose FaceGen discovery (ms) | 0 | 0 | −0 | -39.3% | 0–0 / 0–0 | 0.048 | clear |
|   archive association (plugin -> BSA) (ms) | 2 | 1 | −1 | -39.5% | 2–2 / 1–1 | 0.048 | clear |
|   archive FaceGen indexing (ms) | 0 | 1 | +0 | +129.2% | 0–0 / 1–1 | 0.048 | clear |

| Logical counter | Legacy | Optimized |
|---|---:|---:|
| LooseFoldersScanned | 1 | 1 |
| LooseRootsEnumerated | 2 | 2 |
| LooseCandidatePaths | 6 | 6 |
| LoosePathsRetained | 6 | 6 |
| PreliminaryCandidates | 1 | 1 |
| ArchivesConsidered | 1 | 1 |
| ArchivesFullyIndexed | 1 | 0 |
| ArchiveFileEntriesEnumerated | 37 | 0 |
| ArchiveListingCacheHits | 0 | 1 |
| PluginDirectoryListings | 2 | 2 |
| PluginLoads | 5 | 5 |
