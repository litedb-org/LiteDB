# Completed paired timing evidence (incremental)

Ratios after/before from independent hosted jobs; each job retains six interleaved rounds. Report-only subsets do not issue a release gate verdict.

|Probe|OS/runtime|Scenario/config|Ratios by repetition|Extra allocated bytes (median across jobs)|
|---|---|---|---|---:|
|research-phase2|Linux net10.0|durable_insert_5k_1_writers/dev-default|0.9934, 0.9907|0|
|research-phase2|Linux net10.0|durable_insert_5k_8_writers/dev-default|1.0251, 0.9895|2.37287e+06|
|research-phase2|Linux net10.0|open_close_20x/dev-default|0.9927, 1.0049|2240|
|research-phase2|Linux net10.0|open_close_20x/dev-os-cache|1.0088, 1.0110|2240|
|research-phase2|Linux net8.0|durable_insert_5k_1_writers/dev-default|0.9741|0|
|research-phase2|Linux net8.0|durable_insert_5k_8_writers/dev-default|0.9965|1.07195e+06|
|research-phase2|Linux net8.0|open_close_20x/dev-default|1.0003|2240|
|research-phase2|Linux net8.0|open_close_20x/dev-os-cache|1.0458|2240|
|research-phase2|Windows net8.0|full_scan_3x100k/dev-default|1.0043|0|
|research-phase2|Windows net8.0|full_scan_3x100k/dev-os-cache|0.9915|0|
|research-phase2|Windows net8.0|open_close_20x/dev-default|0.9982|2240|
|research-phase2|Windows net8.0|open_close_20x/dev-os-cache|0.9833|2240|
|research-phase3|Linux net10.0|durable_insert_5k_1_writers/dev-default|1.0383|0|
|research-phase3|Linux net10.0|durable_insert_5k_8_writers/dev-default|0.9934|-741708|
|research-phase3|Linux net10.0|open_close_20x/dev-default|1.1146|12480|
|research-phase3|Linux net10.0|open_close_20x/dev-os-cache|1.0982|12480|
|research-phase3|Linux net8.0|durable_insert_5k_1_writers/dev-default|1.0050, 0.9913|0|
|research-phase3|Linux net8.0|durable_insert_5k_8_writers/dev-default|0.9971, 0.9794|281626|
|research-phase3|Linux net8.0|open_close_20x/dev-default|1.0597, 1.0810|12480|
|research-phase3|Linux net8.0|open_close_20x/dev-os-cache|1.0838, 1.0742|12480|
|research-phase3|Windows net10.0|full_scan_3x100k/dev-default|0.9994|0|
|research-phase3|Windows net10.0|full_scan_3x100k/dev-os-cache|1.0064|0|
|research-phase3|Windows net10.0|open_close_20x/dev-default|1.2009|5.30976e+06|
|research-phase3|Windows net10.0|open_close_20x/dev-os-cache|1.1921|5.30976e+06|
|research-phase3|Windows net8.0|full_scan_3x100k/dev-default|0.9916|0|
|research-phase3|Windows net8.0|full_scan_3x100k/dev-os-cache|0.9914|0|
|research-phase3|Windows net8.0|open_close_20x/dev-default|1.1950|5.30976e+06|
|research-phase3|Windows net8.0|open_close_20x/dev-os-cache|1.2306|5.30976e+06|
|research-phase4|Linux net10.0|durable_insert_5k_1_writers/dev-default|0.9729|1.32007e+06|
|research-phase4|Linux net10.0|durable_insert_5k_8_writers/dev-default|1.2524|-4.05352e+06|
|research-phase4|Linux net10.0|open_close_20x/dev-default|1.0699|28480|
|research-phase4|Linux net10.0|open_close_20x/dev-os-cache|1.0396|28480|
|research-phase4|Linux net8.0|durable_insert_5k_1_writers/dev-default|1.0242|1.32009e+06|
|research-phase4|Linux net8.0|durable_insert_5k_8_writers/dev-default|1.2394|-1.68164e+06|
|research-phase4|Linux net8.0|open_close_20x/dev-default|1.0310|28480|
|research-phase4|Linux net8.0|open_close_20x/dev-os-cache|1.0248|28480|
|research-phase4|Windows net10.0|full_scan_3x100k/dev-default|1.0418|3.84011e+07|
|research-phase4|Windows net10.0|full_scan_3x100k/dev-os-cache|1.0202|3.84011e+07|
|research-phase4|Windows net10.0|open_close_20x/dev-default|1.0191|28480|
|research-phase4|Windows net10.0|open_close_20x/dev-os-cache|0.9699|28480|
|research-phase4|Windows net8.0|full_scan_3x100k/dev-default|1.0540|3.84011e+07|
|research-phase4|Windows net8.0|full_scan_3x100k/dev-os-cache|1.0519|3.84011e+07|
|research-phase4|Windows net8.0|open_close_20x/dev-default|0.9964|28480|
|research-phase4|Windows net8.0|open_close_20x/dev-os-cache|0.9937|28480|
