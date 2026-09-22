import os, hashlib, sys

base = sys.argv[1] if len(sys.argv) > 1 else '.'
rows = []
for root, dirs, files in os.walk(base):
    if '_extract' in root:
        continue
    for f in files:
        if f.endswith('.json'):
            continue
        p = os.path.join(root, f)
        try:
            sz = os.path.getsize(p)
            with open(p, 'rb') as fh:
                h = hashlib.sha256(fh.read()).hexdigest()
        except Exception as e:
            continue
        rel = os.path.relpath(p, base).replace(os.sep, '/')
        rows.append((rel, sz, h))

rows.sort()
for rel, sz, h in rows:
    print("%10d  %s  %s" % (sz, h, rel))
print()
print("TOTAL FILES:", len(rows))
