"""Summarise a REAPERSTUDIO_TRACE log: frame gaps, send times, effect writes."""
import re, sys
L = open(sys.argv[1]).read().splitlines()
eff = [l for l in L if "EFFECT" in l]
print(len(L), "lines;", len(eff), "EFFECT writes")
for l in eff[:10]:
    print("  ", l)
g = sorted(float(m) for l in L for m in re.findall(r"gap=([\d.]+)ms", l))[:-1]
s = sorted(float(m) for l in L for m in re.findall(r"send=([\d.]+)ms", l))
if g:
    print(f"frames={len(s)} gap median={g[len(g)//2]:.0f} p95={g[int(len(g)*.95)]:.0f} max={g[-1]:.0f} ms;"
          f" send median={s[len(s)//2]:.0f} max={s[-1]:.0f} ms; gaps>120ms: {sum(x > 120 for x in g)}")
