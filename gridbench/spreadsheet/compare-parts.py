import os, re, sys

a, b = sys.argv[1], sys.argv[2]
uid = re.compile(rb'\{[0-9A-F-]{36}\}')
ts  = re.compile(rb'20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z')

def norm(path):
    data = open(path, 'rb').read()
    return ts.sub(b'TS', uid.sub(b'UID', data))

names_a, names_b = sorted(os.listdir(a)), sorted(os.listdir(b))
if names_a != names_b:
    print('PART LISTS DIFFER'); print(set(names_a) ^ set(names_b)); sys.exit(1)

differing = [n for n in names_a if norm(os.path.join(a, n)) != norm(os.path.join(b, n))]

for n in differing:
    print('DIFFERS:', n)

print(f'{len(names_a)} parts compared, {len(differing)} differing')
sys.exit(1 if differing else 0)
