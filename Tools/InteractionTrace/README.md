# Tools/InteractionTrace

Analyser for FF's opt-in interaction trace (#1762, epic #1756 S6). Full description:
[Docs/Technical/Interaction-Trace.md](../../Docs/Technical/Interaction-Trace.md).

| File | What it does |
|---|---|
| `analyze_interaction_trace.py` | Splits a trace into interactions (drags, pans, edits) and reports, for each, input arrival and handler time, UI-thread lag, pacer decisions, Trigger cost, calc / barrier / upload / Present time, frames landed and gaps, input-to-screen latency, and which epic slice the numbers point at. `--json` for raw stats, `--selftest` for the built-in check. Python 3, standard library only. |

```bash
# record: start FF with FF_INTERACTION_TRACE=1, drag, close FF; then
python Tools/InteractionTrace/analyze_interaction_trace.py "$TEMP/ff_interaction_trace.csv"
python Tools/InteractionTrace/analyze_interaction_trace.py --selftest
```
