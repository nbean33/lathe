# LatheStudio

Simple CAD → CAM → machine control for a 2-axis hobby lathe running **grblHAL** (lathe mode), in one Windows app (C#, WPF, .NET 10).

```
dotnet run --project src/Lathe.App          # the app
dotnet test LatheStudio.slnx                 # 65 tests incl. end-to-end runs on the virtual lathe
```

## Workflow (the four tabs)

1. **Part:** stock (bar Ø, stick-out from the jaws, face allowance, tube bore), then features from the face toward the chuck:
   - diameters, tapers and arcs (ball/cove) with chamfer or radius on either corner;
   - bores, grooves, and threads (metric coarse/fine, UNC/UNF, custom).

   Validation runs live, and the drawing updates as you type.
2. **Tools:** turning (C/D/V/T/W/round inserts), boring bar, grooving, parting, OD/ID threading, drill and center drill. Each tool gets a real outline that is used for collision checks.
3. **Program:** operations are face, OD/ID rough, OD/ID finish, center drill, peck drill, groove, thread and part off. **Generate** (F5) builds the toolpaths, then simulates material removal and checks for problems:
   - rapids through material;
   - the holder rubbing the work;
   - chuck hits;
   - gouges into the part;
   - leftover material.

   Feeds and speeds come from a material table, capped by machine and chuck limits.
4. **Machine:** connect to a serial port, or pick **Simulator (virtual lathe)** to practise. From there you get:
   - home, unlock and reset;
   - jogging (buttons or arrow keys, hold-to-jog or fixed steps);
   - spindle, coolant and overrides;
   - MDI;
   - touch-off: Set Z0, then Set X from a measured diameter, plus learning each tool's offsets;
   - `$C` program check, Run, Run-from-operation, and Air cut (+20 mm Z);
   - Hold, Resume and Stop.

   The live view shows the tool outline at the machine position.

## Safety design

- **Run is disabled until a preflight checklist passes:**
  - the program is generated and up to date, with no errors;
  - the machine is connected and Idle in lathe mode;
  - work zero is set in this session;
  - the installed tool is confirmed;
  - spindle sync is present if the program threads;
  - RPM and feed are within limits;
  - the program fits machine travel.
- A confirmation dialog summarises the stock, the first tool and work zero before motion.
- Any controller `error:`/`ALARM` while streaming means **feed hold + reset immediately**. No further lines are ever sent after a fault. A watchdog aborts if status reports stop for 1 s.
- Every operation starts its own spindle and ends at the tool-change position with the spindle stopped. That keeps tool changes and run-from-operation self-contained.
- At every tool change the program pauses (spindle off) and asks you to confirm the tool before applying its offsets.
- Threading is refused without spindle sync; unsynced thread moves are never emitted.
- Work zero is never carried between sessions (re-chucking moves it).
- Projects save atomically with a `.bak`, autosave every 60 s, and offer crash recovery. Bad files give readable errors, and corrupt settings fall back to defaults. Unhandled UI errors are logged and feed-hold a running program.
- **The software Stop/Hold is not an E-stop.** The lathe needs a hardware E-stop.

## Conventions

- Internally everything is millimetres and radius. The UI and G-code use **diameter** for X (G7) and mm or inch per project (G21/G20).
- Z0 = finished right face, with −Z toward the chuck.
- Tool control points (what you touch off with):
  - turning: bottom/left of the nose;
  - boring: top/left of the nose;
  - grooving and parting: front edge and **right** side;
  - threading and drills: the tip.
- Feeds are per revolution (G95) when the spindle has an encoder, otherwise G94.
- Program directives `(LS:OP n …)` and `(LS:TOOL n)` are comments to other senders.

## Check these on your real machine (first air cut)

The virtual lathe follows these grblHAL lathe-mode assumptions. Confirm them on your controller before cutting:

1. With G7 active, reported MPos/WPos/WCO X and `G10 L2/L20` X values are **diameters**.
2. `$130` (X travel) is the radial travel, so machine X spans `[-2·$130, 0]` in diameter after homing.
3. `$I` reports spindle sync (NEWOPT `SS`), and `G33 … K<pitch>` cuts synchronized passes.

Run the first program with **Air cut** ticked and your hand on the E-stop.

## Not included (yet)

- Left-hand or multi-start threads.
- Constant surface speed (G96).
- Back chamfers on the parted face.
- Tailstock modelling.
- DXF import.
- Dark theme.

Arcs are output as short G1 segments (0.002 mm chord tolerance).

## Layout

| Project | Contents |
|---|---|
| `src/Lathe.Core` | Geometry, part model and validation, tools, materials, threads, project files |
| `src/Lathe.Cam` | Toolpaths (Clipper2 configuration-space obstacles), stock simulator, grblHAL post, preflight |
| `src/Lathe.Machine` | grblHAL protocol, serial transport, streamer, virtual grblHAL |
| `src/Lathe.App` | WPF UI |
| `tests/` | Unit, golden G-code, and end-to-end (CAM → virtual controller) tests |

Settings, machine limits (`machine.json`), logs and autosave live in `%AppData%\LatheStudio` (Help → Open data folder).
