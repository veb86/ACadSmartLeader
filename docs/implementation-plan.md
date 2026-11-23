# Issue 3 implementation and verification plan

This plan records PR #4. For current command-line operation and the design of a future menu, see the [AutoCAD menu specification](TZ-autocad-menu.md).

Objective: implement the C# smart leader described in `TZ-smart-leader.md` in PR #4.
Branch: `issue-3-7181973aa3f2`. No required sub-issues or additional feedback were present at initial review.

## Work sequence

1. Pin the palette Core submodule at v0.0.143; add the projects, reusable rule/data/evaluation/update logic and focused xUnit coverage.
2. Implement the AutoCAD adapter: geometric binding, NOD storage/cache, creation and transactional updates, commands, document lifecycle and deferred REGEN updates.
3. Add WPF editors/settings, ribbon/context commands, Autoloader packaging, reproducible demonstration and acceptance scripts.
4. Run focused tests, compile both plugin targets in CI, inspect the PR diff, update the description and mark ready once implementation checks pass.

## Requirements inventory

| Specification | Implementation required | Verification |
| --- | --- | --- |
| 5, 15 | C#, pinned source reuse, embedded NCalc 7.4.0, net48/net8 Windows, single plugin DLL, bundle, CI | Both target builds, packaging checks, Core tests |
| 6 | Native MLeader, one/two shelves, style/layer/annotation inheritance | Adapter build; AutoCAD 2021/2025 drawing and visual acceptance |
| 7 | BobrovLeader v1 codec, target handle/GUID, overrides, state, last-text hash and byte limits | Codec round trip and invalid/large input tests |
| 8 | Ordered rules, selection, JSON extensions/validation, NOD LABELS records, default persistence | Core tests; AutoCAD import/export/reopen acceptance |
| 9 | Arrow geometry/tolerance, draw-order tie, blocks to depth 4, same-space lookup, rebind/GUID/detach | Geometry/resolution tests; COPY/WBLOCK/INSERT/erase/Undo acceptance |
| 10 | Actual palette calculation, defaults/enums/precision/culture, errors/empty values, MText escape, schema cache | Real example schema evaluation tests |
| 11 | PickFirst/All/Select/Force, reports, problems, locked/xref skip, changed-only writes, one Undo | Update-plan tests; AutoCAD transactional acceptance |
| 11.2 | Off/Regen/RegenAll, Idle scheduling, no reentry, per-document cleanup | Scheduler tests; AutoCAD REGEN/LISP/multi-document acceptance |
| 12 | All eight SL commands, preview/parameter insertion/rebind, ribbon/context menu | Both target builds; AutoCAD WPF and menu acceptance |
| 13, 14 | Missing schema/data, schema changes, offscreen/all-space search, target reuse, 1000-leader performance | Core tests; AutoCAD demo/performance acceptance |
| 16–18 | Demo drawing, both palette load orders, persistence without plugin, runtime/visual risks | Reproducible acceptance commands and results, explicitly distinguish unrun CAD checks |

Section 17 stage 5 describes extensions accepted “по отдельным задачам”, and sections 6.3/19 explicitly choose native MText and one arrow for this specification. These future block/multiple-target modes are not prerequisites for stages 1–4. No such separate tasks are linked by issue #3.

## Environment limits

The development host is Linux and provides .NET SDK 10 but no AutoCAD runtime or Windows desktop session. Compilation and Core tests are executable here/CI. Real AutoCAD visual/Undo/co-loading/performance acceptance and production of an authentic `demo.dwg` require a licensed Windows AutoCAD host. These remain unfinished until actually run; reference assemblies do not establish runtime acceptance.

## Status

- [x] Read issue #3, all issue comments, PR #4 conversation/inline/reviews, full specification and related merged PR #2.
- [x] Core implementation and focused tests (13 passing locally).
- [x] AutoCAD implementation and WPF interface (dual-target build passed in CI; runtime acceptance pending).
- [x] Bundle, CI and acceptance tooling (Windows CI execution passed).
- [x] Implementation CI, artifact inspection and final code review (runs below; final documentation commit is checked by the same workflow before handoff).
- [ ] AutoCAD 2021/2025 runtime, screenshots, authentic demo DWG and performance acceptance (environment dependency).

## Investigation evidence

- CI run 44055895001, commit 2db698d: Core and both plugin targets passed; menu generation failed with InvalidProgramException at CustomizationSection.MenuGroup (saved log lines 398–401). NuGet supplies reference-only implementations. Replaced the executable AutoCAD menu builder with a CUIX XML/ZIP generator based on Autodesk ADN's MIT sample. Native menu loading remains a CAD acceptance item.
- An isolated DLL smoke check found no embedded dependencies in the initial .NET 8 library output. Setting CopyLocalLockFileAssemblies populates the embedding target. A fresh process now loads only the plugin bytes and evaluates MOTOR_FULL with its seven embedded dependencies; Windows CI also checks the .NET Framework build in a .NET Framework process.
- CI run 44057254497, commit a476390: Core's 13 tests and both plugin targets passed. The Framework smoke-check build failed with CS0656, missing Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo.Create (saved log lines 170–175). Added the explicit Microsoft.CSharp framework reference; both smoke-check targets compile locally without warnings. Windows runtime results are recorded below.
- [CI run 44058181022](https://github.com/konard/veb86-ACadSmartLeader/actions/runs/44058181022), commit 4bb684f: both jobs passed. Saved log lines 184/191 show isolated formula evaluation with 16 embedded dependencies on Framework and seven on .NET 8; line 217 confirms package validation; line 436 reports 13 passing Core tests. Inspected all three WPF render images. The final specification review added parameter insertion to SL_EDIT as well as SL_RULES; the render tool now captures both scroll positions of a long dialog.
- [Final implementation CI run 44059519473](https://github.com/konard/veb86-ACadSmartLeader/actions/runs/44059519473), commit fb3d11c: both jobs passed, including WPF parameter insertion into both shelves and reinsertion after disabling an override. The latter regression left the preview stale when only Click was observed; Checked/Unchecked now refresh the preview for programmatic activation as well. Render output checks the opaque theme background. Downloaded and inspected the four PNGs in `docs/screenshots/`, and validated the downloaded bundle locally. Core still has 13 passing tests.
- Final issue/PR conversation, inline and review API reads found no new feedback. Fetched upstream main and verified it is already an ancestor of this branch. The PR diff preserves all original experiments and specification files.
