#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Bradley Brown
"""Analyse an FF interaction trace (#1762, interactivity epic #1756 S6).

Record a trace by starting FF with FF_INTERACTION_TRACE=1; it writes
%TEMP%/ff_interaction_trace.csv (rewritten at each start). Then:

    python Tools/InteractionTrace/analyze_interaction_trace.py [trace.csv] [--json] [--gap-ms 400]

The trace is split into interactions: runs of input (pointer moves, presses, wheel steps, keys,
render requests) with no gap longer than --gap-ms. For each it reports where the time went, from
the pointer event's OS time stamp to the frame on screen, and which epic slice each number bears
on (S2 overlap calc with Present, S3 2D frames finish, S4 Math Plane 3D speed, S5 UI-thread
churn, S7 upload post pipeline). See Docs/Technical/Interaction-Trace.md.

--selftest checks the analysis against a small hand-built trace with known answers.
Standard library only.
"""

import bisect
import json
import os
import statistics
import sys
import tempfile
from collections import defaultdict

KINDS = {
    1: 'PointerMove', 2: 'InputHandled', 3: 'InputOther', 4: 'RenderHint', 5: 'PacerRequest',
    6: 'PacerFire', 7: 'TriggerEnter', 8: 'TriggerExit', 9: 'CalcStart', 10: 'UploadBarrier',
    11: 'CalcEnd', 12: 'UploadQueued', 13: 'UploadStart', 14: 'PostPipeline', 15: 'Present',
    16: 'Landed', 17: 'Dropped', 18: 'UploadEnd', 19: 'UiLag', 20: 'Resize',
}
INPUT_KINDS = {'PointerMove', 'InputOther', 'RenderHint', 'PacerRequest', 'Resize'}
INPUT_NAMES = {0: 'move', 1: 'press', 2: 'release', 3: 'wheel', 4: 'key'}
HINT_NAMES = {0: 'full', 1: 'fast', 2: 'interactive'}
DROP_NAMES = {1: 'cancelled', 2: 'final superseded', 3: 'preview superseded'}
FLAG_KEPT, FLAG_CANCELLED, FLAG_PROGRESSIVE, FLAG_3D, FLAG_UI = 1, 2, 4, 8, 16
FRAME_MS = 1000.0 / 60   # a display refresh, for the verdicts


class Row:
    __slots__ = ('t', 'thread', 'kind', 'seq', 'a', 'b')

    def __init__(self, t, thread, kind, seq, a, b):
        self.t, self.thread, self.kind, self.seq, self.a, self.b = t, thread, kind, seq, a, b


def parse(path):
    rows, types, dropped = [], {}, 0
    with open(path, encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith('# fractal_types:'):
                for tok in line.split(':', 1)[1].split():
                    k, _, v = tok.partition('=')
                    types.setdefault(int(k), v)
                continue
            if line.startswith('# dropped '):
                dropped += int(line.split()[2])
                continue
            if line.startswith('#') or line.startswith('t_us'):
                continue
            p = line.split(',')
            kind = p[2] if not p[2].isdigit() else KINDS.get(int(p[2]), p[2])
            rows.append(Row(int(p[0]), int(p[1]), kind, int(p[3]), int(p[4]), int(p[5])))
    rows.sort(key=lambda r: r.t)
    return rows, types, dropped


def pct(xs, q):
    if not xs:
        return None
    s = sorted(xs)
    i = min(len(s) - 1, max(0, int(round(q / 100.0 * (len(s) - 1)))))
    return s[i]


def summary(xs, scale=1.0):
    """count / p50 / p95 / max / mean of xs, in units of xs*scale."""
    if not xs:
        return {'n': 0}
    return {'n': len(xs), 'p50': round(pct(xs, 50) * scale, 2), 'p95': round(pct(xs, 95) * scale, 2),
            'max': round(max(xs) * scale, 2), 'mean': round(statistics.fmean(xs) * scale, 2)}


def segments(rows, gap_us):
    """Interactions: runs of input records with no gap longer than gap_us."""
    starts = [r.t for r in rows if r.kind in INPUT_KINDS]
    segs = []
    for t in starts:
        if segs and t - segs[-1][1] <= gap_us:
            segs[-1][1] = t
        else:
            segs.append([t, t])
    return segs


def analyse(rows, types, gap_ms=400.0, tail_ms=1500.0):
    gap_us, tail_us = gap_ms * 1000, tail_ms * 1000
    segs = segments(rows, gap_us)
    out = []
    in_any = []
    # Frames begun before an interaction's first input (a resize lands a frame started earlier).
    g_cstart = {r.seq: r for r in rows if r.kind == 'CalcStart'}
    g_ustart = {r.seq: r for r in rows if r.kind == 'UploadStart'}
    for idx, (s0, s1) in enumerate(segs):
        end = s1 + tail_us
        nxt = segs[idx + 1][0] if idx + 1 < len(segs) else None
        if nxt is not None:
            end = min(end, nxt)
        win = [r for r in rows if s0 <= r.t <= end]
        in_any.append((s0, end))
        out.append(analyse_segment(win, s0, s1, types, g_cstart, g_ustart))
    # UI lag outside interactions: the idle baseline the in-drag lag is compared with.
    idle = [r.a for r in rows if r.kind == 'UiLag' and not any(a <= r.t <= b for a, b in in_any)]
    return {'interactions': out, 'ui_lag_idle_ms': summary(idle, 1e-3)}


def analyse_segment(win, s0, s1, types, g_cstart=None, g_ustart=None):
    by = defaultdict(list)
    for r in win:
        by[r.kind].append(r)
    dur_s = max(1e-6, (s1 - s0) / 1e6)
    res = {'start_s': round(s0 / 1e6, 3), 'input_s': round(dur_s, 3)}

    # == Input (UI thread) ==
    moves = by['PointerMove']
    others = by['InputOther']
    res['pointer_moves'] = len(moves)
    res['moves_per_s'] = round(len(moves) / dur_s, 1)
    res['coalesced_points'] = summary([r.b for r in moves])
    res['os_to_handler_ms'] = summary([r.a for r in moves + others if r.a >= 0])
    handled = defaultdict(list)
    for r in by['InputHandled']:
        handled[INPUT_NAMES.get(r.b, str(r.b))].append(r.a)
    res['handler_ms'] = {k: summary(v, 1e-3) for k, v in sorted(handled.items())}
    res['other_input'] = dict(sorted(
        _count(INPUT_NAMES.get(r.b, str(r.b)) for r in others).items()))
    res['render_hints'] = dict(sorted(_count(HINT_NAMES.get(r.a, str(r.a)) for r in by['RenderHint']).items()))
    res['ui_lag_ms'] = summary([r.a for r in by['UiLag']], 1e-3)

    # == Pacer ==
    pr = by['PacerRequest']
    res['pacer'] = {'requests': len(pr), 'started': sum(r.a == 0 for r in pr),
                    'forced': sum(r.a == 1 for r in pr), 'collapsed': sum(r.a == 2 for r in pr),
                    'fired_on_landing': sum(r.a == 0 for r in by['PacerFire']),
                    'fired_by_watchdog': sum(r.a == 1 for r in by['PacerFire'])}

    # == Trigger ==
    open_enter = {}
    trig = []   # (enter_t, exit row, fractal type)
    for r in win:
        if r.kind == 'TriggerEnter':
            open_enter[r.thread] = r
        elif r.kind == 'TriggerExit' and r.thread in open_enter:
            e = open_enter.pop(r.thread)
            trig.append((e.t, r, e.b))
    res['triggers'] = len(trig)
    res['triggers_per_s'] = round(len(trig) / dur_s, 1)
    res['fractal_types'] = dict(sorted(_count(types.get(ft, str(ft)) for _, _, ft in trig).items()))
    res['trigger_ms'] = summary([x.t - t for t, x, _ in trig], 1e-3)
    res['apply_view_ms'] = summary([x.a for _, x, _ in trig], 1e-3)
    res['trigger_on_ui_thread'] = sum(1 for _, x, _ in trig if x.b & FLAG_UI)
    res['kept_in_flight'] = sum(1 for _, x, _ in trig if x.b & FLAG_KEPT)
    res['cancelled_in_flight'] = sum(1 for _, x, _ in trig if x.b & FLAG_CANCELLED)
    is3d = [bool(x.b & FLAG_3D) for _, x, _ in trig]
    res['view'] = '3D' if is3d and all(is3d) else ('2D' if is3d and not any(is3d) else ('mixed' if is3d else '-'))
    trig_exit_by_seq = {x.seq: x for _, x, _ in trig}

    # == Calc thread ==
    cstart = {r.seq: r for r in by['CalcStart']}
    calc = defaultdict(list)
    cancelled = 0
    busy = 0
    for r in by['CalcEnd']:
        st = cstart.get(r.seq)
        if st is None:
            continue
        busy += r.t - st.t
        if r.b:
            cancelled += 1
        else:
            calc['final' if r.a == 0 else 'preview'].append(r.t - st.t)
    res['calc_ms'] = {k: summary(v, 1e-3) for k, v in sorted(calc.items())}
    res['calc_cancelled'] = cancelled
    res['queue_wait_ms'] = summary([cstart[s].t - x.t for s, x in trig_exit_by_seq.items()
                                    if s in cstart and cstart[s].t >= x.t], 1e-3)
    barrier = [r.a for r in by['UploadBarrier']]
    res['upload_barrier_ms'] = summary(barrier, 1e-3)

    # == Upload / present ==
    uq = {r.seq: r for r in by['UploadQueued']}
    ustart = {r.seq: r for r in by['UploadStart']}
    res['pool_delay_ms'] = summary([r.t - uq[r.seq].t for r in by['UploadStart'] if r.seq in uq], 1e-3)
    res['upload_ms'] = summary([r.t - ustart[r.seq].t for r in by['UploadEnd'] if r.seq in ustart], 1e-3)
    res['post_pipeline_ms'] = summary([r.a for r in by['PostPipeline']], 1e-3)
    pres = defaultdict(list)
    for r in by['Present']:
        pres['final' if r.b == 0 else ('preview' if r.b > 0 else 'other')].append(r.a)
    res['present_ms'] = {k: summary(v, 1e-3) for k, v in sorted(pres.items())}
    present_busy = sum(r.a for r in by['Present'])

    # == Frames on screen ==
    landed = by['Landed']
    lt = [r.t for r in landed if r.t <= s1] if landed else []
    res['landed'] = len(landed)
    res['landed_during_input'] = len(lt)
    res['landed_per_s'] = round(len(lt) / dur_s, 1)
    gaps = [b - a for a, b in zip([s0] + lt, lt + [s1])] if s1 > s0 else []
    res['landed_gap_ms'] = summary(gaps, 1e-3)
    res['dropped'] = dict(sorted(_count(DROP_NAMES.get(r.a, str(r.a)) for r in by['Dropped']).items()))

    # == Input to screen ==
    # A move is shown by the first frame landed from a Trigger that began after it (that Trigger
    # read the moved view; a later frame shows it too). OS time adds the event's wait before its
    # handler ran, when the platform stamps it on the tick-count clock.
    trig_sorted = sorted(trig, key=lambda x: x[0])
    trig_t = [t for t, _, _ in trig_sorted]
    land_sorted = sorted(landed, key=lambda r: r.t)
    lat, lat_os, unshown = [], [], 0
    drivers = moves or by['RenderHint'] or pr
    for m in drivers:
        i = bisect.bisect_left(trig_t, m.t)
        if i >= len(trig_sorted):
            unshown += 1
            continue
        seq = trig_sorted[i][1].seq
        hit = next((r for r in land_sorted if r.seq >= seq and r.t >= m.t), None)
        if hit is None:
            unshown += 1
            continue
        lat.append(hit.t - m.t)
        if m.kind == 'PointerMove' and m.a >= 0:
            lat_os.append(hit.t - m.t + m.a * 1000)
    res['handler_to_screen_ms'] = summary(lat, 1e-3)
    res['os_to_screen_ms'] = summary(lat_os, 1e-3)
    res['input_not_shown'] = unshown

    # == Occupancy over the input window ==
    span = max(1.0, (s1 - s0))
    res['calc_busy_pct'] = round(100.0 * busy / span, 1)
    res['barrier_wait_pct'] = round(100.0 * sum(barrier) / span, 1)
    res['present_busy_pct'] = round(100.0 * present_busy / span, 1)
    # -- Window resize --
    # A resize presents the resized swap chain (Present stage -2), then re-renders. A frame begun
    # before that present and shown after it was calculated for the old size and is stretched
    # into the new one.
    rs = by['Resize']
    res['resizes'] = len(rs)
    if rs:
        rp = sorted(r.t for r in by['Present'] if r.b == -2)
        block = []
        for x in rs:
            i = bisect.bisect_left(rp, x.t)
            if i < len(rp):
                block.append(rp[i] - x.t)
        res['resize_to_swapchain_ms'] = summary(block, 1e-3)
        gcs = g_cstart or cstart
        gus = g_ustart or ustart
        stale = 0
        for l in landed:
            st = gcs.get(l.seq)
            if st is None:
                continue
            i = bisect.bisect_right(rp, st.t)
            if i < len(rp) and rp[i] < l.t:
                stale += 1
        res['old_size_frames_after_resize'] = stale
        res['upload_running_at_resize'] = sum(
            1 for r in by['UploadEnd'] if r.seq in gus
            and any(gus[r.seq].t < x.t < r.t for x in rs))
    res['hints'] = hints(res)
    return res


def _count(it):
    d = defaultdict(int)
    for x in it:
        d[x] += 1
    return dict(d)


def _g(d, *path):
    for p in path:
        if not isinstance(d, dict) or p not in d:
            return None
        d = d[p]
    return d


def hints(r):
    """Plain verdicts tying the numbers to the epic's slices."""
    h = []
    os95 = _g(r, 'os_to_handler_ms', 'p95')
    hm95 = _g(r, 'handler_ms', 'move', 'p95')
    lag95 = _g(r, 'ui_lag_ms', 'p95')
    if os95 is not None and os95 > FRAME_MS:
        h.append(f'S5/S6: pointer events waited {os95:.0f} ms (p95) before the UI thread handled them; the UI thread is busy.')
    if hm95 is not None and hm95 > 4:
        h.append(f'S5: the move handler takes {hm95:.1f} ms (p95) on the UI thread.')
    if lag95 is not None and lag95 > 8:
        h.append(f'S5: work posted to the UI thread waited {lag95:.1f} ms (p95).')
    bw = r.get('barrier_wait_pct', 0)
    if bw > 10:
        b50 = _g(r, 'upload_barrier_ms', 'p50') or 0
        h.append(f'S2: the calc thread spent {bw:.0f}% of the drag waiting for the previous upload/Present '
                 f'({b50:.1f} ms median); a second upload buffer would overlap that.')
    pb, cb = r.get('present_busy_pct', 0), r.get('calc_busy_pct', 0)
    pres50 = _g(r, 'present_ms', 'preview', 'p50') or _g(r, 'present_ms', 'final', 'p50')
    calc50 = _g(r, 'calc_ms', 'preview', 'p50') or _g(r, 'calc_ms', 'final', 'p50')
    if pb > 50 and cb < 50 and pres50 and calc50 and r.get('landed_per_s'):
        est = 1000.0 / max(pres50, calc50)
        h.append(f'S2: calculation and Present run one after the other: Present is busy {pb:.0f}% of the drag, '
                 f'the calc thread {cb:.0f}%. Overlapping them (calc {calc50:.1f} ms, Present {pres50:.1f} ms, median) '
                 f'could take {r["landed_per_s"]:.0f} frames/s toward {est:.0f}.')
    if r.get('view') == '2D' and (r.get('cancelled_in_flight', 0) or r.get('calc_cancelled', 0)):
        h.append(f"S3: {r.get('calc_cancelled', 0)} 2D frames were cancelled mid-calculation "
                 f"({r.get('cancelled_in_flight', 0)} by a Trigger); a view snapshot would let them finish.")
    if 'MathPlane3D' in r.get('fractal_types', {}):
        c50 = _g(r, 'calc_ms', 'final', 'p50') or _g(r, 'calc_ms', 'preview', 'p50')
        if c50 and c50 > FRAME_MS:
            h.append(f'S4: Math Plane 3D frames take {c50:.0f} ms (median) to calculate.')
    post50 = _g(r, 'post_pipeline_ms', 'p50')
    fin50 = _g(r, 'calc_ms', 'final', 'p50')
    if post50 and fin50 and post50 > 0.25 * (post50 + fin50):
        h.append(f'S7: the post pipeline is {post50:.1f} ms of a {post50 + fin50:.1f} ms final frame (median).')
    pd95 = _g(r, 'pool_delay_ms', 'p95')
    if pd95 and pd95 > 4:
        h.append(f'S7: uploads waited {pd95:.1f} ms (p95) for a thread-pool thread.')
    gap = _g(r, 'landed_gap_ms', 'max')
    if gap and gap > 100:
        h.append(f'The longest time with no new frame was {gap:.0f} ms.')
    if r.get('trigger_on_ui_thread') and (_g(r, 'trigger_ms', 'p95') or 0) > 4:
        h.append(f"S5: Trigger costs {_g(r, 'trigger_ms', 'p95'):.1f} ms (p95) and ran on the UI thread {r['trigger_on_ui_thread']} times.")
    if r.get('resizes'):
        lag_max = _g(r, 'ui_lag_ms', 'max') or 0
        h.append(f"S8 resize: {r['resizes']} resize steps; UI lag up to {lag_max:.0f} ms; "
                 f"{r.get('old_size_frames_after_resize', 0)} frames calculated at the old size were shown after the "
                 f"swap chain was resized; {r.get('upload_running_at_resize', 0)} uploads were reading the buffers when "
                 f"a resize began.")
    if not h:
        h.append('No bottleneck past the thresholds.')
    return h


def fmt(s, unit='ms'):
    if not s or s.get('n', 0) == 0:
        return '-'
    return f"p50 {s['p50']} / p95 {s['p95']} / max {s['max']} {unit} (n={s['n']})"


def report(res):
    lines = []
    for i, r in enumerate(res['interactions'], 1):
        types = ', '.join(f'{k}x{v}' for k, v in r['fractal_types'].items()) or '-'
        lines.append(f"== Interaction {i} at {r['start_s']} s: {r['input_s']} s of input, {r['view']} ({types})")
        lines.append(f"  input        {r['pointer_moves']} moves ({r['moves_per_s']}/s), coalesced points {fmt(r['coalesced_points'], '')}")
        lines.append(f"               other {r['other_input'] or '-'}, hints {r['render_hints'] or '-'}")
        lines.append(f"  OS->handler   {fmt(r['os_to_handler_ms'])}")
        for k, v in r['handler_ms'].items():
            lines.append(f"  handler {k:<5}{fmt(v)}")
        lines.append(f"  UI lag       {fmt(r['ui_lag_ms'])}")
        p = r['pacer']
        lines.append(f"  pacer        {p['requests']} requests: {p['started']} started, {p['forced']} forced, "
                     f"{p['collapsed']} collapsed; fired {p['fired_on_landing']} on landing, {p['fired_by_watchdog']} by watchdog")
        lines.append(f"  Trigger      {r['triggers']} ({r['triggers_per_s']}/s), {fmt(r['trigger_ms'])}; ApplyView {fmt(r['apply_view_ms'])}")
        lines.append(f"               on UI thread {r['trigger_on_ui_thread']}, kept in flight {r['kept_in_flight']}, cancelled in flight {r['cancelled_in_flight']}")
        lines.append(f"  queue wait   {fmt(r['queue_wait_ms'])}")
        lines.append(f"  barrier wait {fmt(r['upload_barrier_ms'])}  ({r['barrier_wait_pct']}% of the drag)")
        for k, v in r['calc_ms'].items():
            lines.append(f"  calc {k:<8}{fmt(v)}")
        lines.append(f"  calc cancelled {r['calc_cancelled']}, calc busy {r['calc_busy_pct']}% of the drag")
        lines.append(f"  pool delay   {fmt(r['pool_delay_ms'])}")
        lines.append(f"  post pipe    {fmt(r['post_pipeline_ms'])}")
        for k, v in r['present_ms'].items():
            lines.append(f"  present {k:<8}{fmt(v)}")
        lines.append(f"  upload total {fmt(r['upload_ms'])}; present busy {r['present_busy_pct']}% of the drag")
        lines.append(f"  landed       {r['landed']} ({r['landed_during_input']} during input, {r['landed_per_s']}/s), gaps {fmt(r['landed_gap_ms'])}")
        lines.append(f"  dropped      {r['dropped'] or '-'}")
        lines.append(f"  to screen    handler->screen {fmt(r['handler_to_screen_ms'])}")
        lines.append(f"               OS->screen {fmt(r['os_to_screen_ms'])}; not shown {r['input_not_shown']}")
        if r.get('resizes'):
            lines.append(f"  resize       {r['resizes']} steps; to swap chain {fmt(r.get('resize_to_swapchain_ms'))}")
            lines.append(f"               old-size frames shown after resize {r['old_size_frames_after_resize']}, uploads running at resize {r['upload_running_at_resize']}")
        for x in r['hints']:
            lines.append(f"  > {x}")
        lines.append('')
    lines.append(f"UI lag while idle: {fmt(res['ui_lag_idle_ms'])}")
    return '\n'.join(lines)


# == Self-test: a hand-built trace with known answers ==

SAMPLE = """# FF interaction trace #1762 v1, started 2026-11-13 10:00:00.000
# fractal_types: 0=Mandelbrot 1=Julia
t_us,thread,kind,seq,a,b
0,1,UiLag,0,500,0
100000,1,PointerMove,0,8,2
100050,1,TriggerEnter,0,0,0
101050,1,TriggerExit,5,600,16
101200,1,InputHandled,0,1200,0
101300,7,CalcStart,5,0,0
121300,7,CalcEnd,5,0,0
121400,7,UploadQueued,5,0,0
122400,9,UploadStart,5,0,0
124400,9,PostPipeline,5,2000,0
138400,9,Present,5,14000,0
138500,9,Landed,5,0,0
138600,9,UploadEnd,5,0,0
110000,1,PointerMove,0,24,1
110100,1,PacerRequest,0,2,0
110200,1,InputHandled,0,200,0
138700,9,PacerFire,0,0,0
138800,9,TriggerEnter,0,0,0
139800,9,TriggerExit,6,900,2
139900,7,CalcStart,6,0,0
140000,7,UploadBarrier,6,3000,0
160000,7,CalcEnd,6,0,1
160100,7,Dropped,6,1,0
130000,1,UiLag,0,12000,0
2000000,1,UiLag,0,1000,0
5000000,1,Resize,0,800,600
5001000,1,Present,-1,300,-2
5001500,1,TriggerEnter,0,0,0
5002000,1,TriggerExit,9,0,16
4990000,7,CalcStart,8,0,0
4999000,9,UploadStart,8,0,0
5003000,9,Present,8,500,0
5003100,9,Landed,8,0,0
5003200,9,UploadEnd,8,0,0
"""


def selftest():
    fd, path = tempfile.mkstemp(suffix='.csv')
    with os.fdopen(fd, 'w', encoding='utf-8') as f:
        f.write(SAMPLE)
    try:
        rows, types, dropped = parse(path)
    finally:
        os.remove(path)
    res = analyse(rows, types, gap_ms=400)
    ok = True

    def check(name, got, want):
        nonlocal ok
        if got != want:
            ok = False
            print(f'FAIL {name}: got {got!r}, want {want!r}')

    check('records', len(rows), 34)
    check('interactions', len(res['interactions']), 2)
    z = res['interactions'][1]
    check('resizes', z['resizes'], 1)
    check('resize to swapchain', z['resize_to_swapchain_ms']['max'], 1.0)
    check('old-size frame after resize', z['old_size_frames_after_resize'], 1)
    check('upload running at resize', z['upload_running_at_resize'], 1)
    r = res['interactions'][0]
    check('moves', r['pointer_moves'], 2)
    check('os delay p50', r['os_to_handler_ms']['p50'], 8)      # [8, 24] -> index round(0.5) = 0
    check('os delay max', r['os_to_handler_ms']['max'], 24)
    check('handler move max', r['handler_ms']['move']['max'], 1.2)
    check('triggers', r['triggers'], 2)
    check('trigger ms max', r['trigger_ms']['max'], 1.0)          # 101050-100050, 139800-138800
    check('apply view max', r['apply_view_ms']['max'], 0.9)
    check('on ui thread', r['trigger_on_ui_thread'], 1)
    check('cancelled in flight', r['cancelled_in_flight'], 1)
    check('types', r['fractal_types'], {'Mandelbrot': 2})
    check('view', r['view'], '2D')
    check('queue wait', r['queue_wait_ms']['max'], 0.25)            # 101300-101050 (seq 5), 139900-139800 (seq 6) -> 0.1, 0.25
    check('calc final', r['calc_ms']['final']['max'], 20.0)
    check('calc cancelled', r['calc_cancelled'], 1)
    check('barrier', r['upload_barrier_ms']['max'], 3.0)
    check('pool delay', r['pool_delay_ms']['max'], 1.0)
    check('post', r['post_pipeline_ms']['max'], 2.0)
    check('present final', r['present_ms']['final']['max'], 14.0)
    check('landed', r['landed'], 1)
    check('dropped', r['dropped'], {'cancelled': 1})
    check('pacer', (r['pacer']['collapsed'], r['pacer']['fired_on_landing']), (1, 1))
    # Move 1 (t=100000) -> Trigger seq 5 -> landed 138500: 38.5 ms; + OS 8 ms = 46.5.
    # Move 2 (t=110000) -> first Trigger after it is seq 6, never landed -> not shown.
    check('to screen', r['handler_to_screen_ms']['max'], 38.5)
    check('os to screen', r['os_to_screen_ms']['max'], 46.5)
    check('not shown', r['input_not_shown'], 1)
    check('ui lag in drag', r['ui_lag_ms']['max'], 12.0)
    check('ui lag idle', (res['ui_lag_idle_ms']['n'], res['ui_lag_idle_ms']['max']), (2, 1.0))   # t=0 and t=2 s
    check('hint S3', any(x.startswith('S3:') for x in r['hints']), True)
    check('hint S5 os', any(x.startswith('S5/S6:') for x in r['hints']), True)
    print('selftest', 'ok' if ok else 'FAILED')
    return 0 if ok else 1


def main(argv):
    args = [a for a in argv if not a.startswith('--')]
    if '--selftest' in argv:
        return selftest()
    gap = 400.0
    if '--gap-ms' in argv:
        gap = float(argv[argv.index('--gap-ms') + 1])
        args = [a for a in args if a != argv[argv.index('--gap-ms') + 1]]
    path = args[0] if args else os.path.join(tempfile.gettempdir(), 'ff_interaction_trace.csv')
    rows, types, dropped = parse(path)
    res = analyse(rows, types, gap_ms=gap)
    res['records'] = len(rows)
    res['records_dropped'] = dropped
    if '--json' in argv:
        print(json.dumps(res, indent=1))
    else:
        print(f'{path}: {len(rows)} records' + (f', {dropped} dropped (ring lapped)' if dropped else ''))
        print(report(res))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
