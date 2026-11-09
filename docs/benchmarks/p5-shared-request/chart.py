"""Standalone SVG of each repetition's median ratio; no pooled runtimes."""
import json
import math
from pathlib import Path
import sys
from xml.sax.saxutils import escape

rows = [r for r in json.loads(Path(sys.argv[1]).read_text()) if r['metric'] == 'latencyMs']
maximum = max(r['ratio'] for r in rows)
top = max(10, 10 ** math.ceil(math.log10(maximum)))
parts = ['<svg xmlns="http://www.w3.org/2000/svg" width="1100" height="800" viewBox="0 0 1100 800">',
         '<rect width="1100" height="800" fill="white"/>',
         '<style>text{font-family:sans-serif;font-size:13px} .title{font-size:19px;font-weight:bold}</style>',
         '<text x="35" y="28" class="title">Shared request v6/v5 latency ratio — each repetition shown separately</text>']
colors = {'Linux x64': '#0072b2', 'Windows x64': '#d55e00', 'macOS ARM64': '#009e73'}
for panel, (version, workers) in enumerate([(8, 1), (8, 4), (10, 1), (10, 4)]):
    left = 75 + (panel % 2) * 540
    upper = 95 + (panel // 2) * 340
    width, height = 425, 235
    def y(value):
        return upper + height * (1 - math.log10(value) / math.log10(top))
    parts.append(f'<text x="{left}" y="{upper-20}">net{version}, {workers} process(es); ratio (log scale)</text>')
    for tick in [1, 1.5, 10, 100, 1000]:
        if tick > top:
            continue
        color = '#999' if tick == 1.5 else '#ddd'
        parts.append(f'<line x1="{left}" y1="{y(tick):.2f}" x2="{left+width}" y2="{y(tick):.2f}" stroke="{color}"/>')
        parts.append(f'<text x="{left-38}" y="{y(tick)+4:.2f}">{tick:g}x</text>')
    for index, scenario in enumerate(['read', 'insert', 'mixed']):
        center = left + 70 + index * 140
        parts.append(f'<text x="{center-25}" y="{upper+height+25}">{scenario}</text>')
        for os_index, (platform, color) in enumerate(colors.items()):
            for rep in (1, 2):
                row = next(r for r in rows if r['platform'] == platform and r['framework'] == f'.NETCoreApp,Version=v{version}.0' and r['workers'] == workers and r['scenario'] == scenario and r['repetition'] == rep)
                x = center + (os_index-1)*30 + (rep-1.5)*8
                title = escape(f"{platform} rep{rep}: {row['ratio']:.2f}x")
                parts.append(f'<circle cx="{x}" cy="{y(row["ratio"]):.2f}" r="5" fill="{color}"><title>{title}</title></circle>')
for index, (platform, color) in enumerate(colors.items()):
    x = 130 + index * 290
    parts.append(f'<circle cx="{x}" cy="760" r="5" fill="{color}"/><text x="{x+12}" y="765">{platform}</text>')
parts.append('<text x="75" y="790">Reference: 1.5x. Two dots per OS/scenario; observed sample bounds are retained in results.md.</text></svg>')
Path(sys.argv[2]).write_text('\n'.join(parts))
