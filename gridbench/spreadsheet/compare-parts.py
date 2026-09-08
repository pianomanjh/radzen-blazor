import os, re, sys

a, b = sys.argv[1], sys.argv[2]
uid = re.compile(rb'\{[0-9A-F-]{36}\}')
ts  = re.compile(rb'20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z')

def norm(path):
    data = open(path, 'rb').read()
    return ts.sub(b'TS', uid.sub(b'UID', data))

# A workbook has these whatever it holds. Without this check two empty directories - which is what a
# generator that failed to build leaves behind - compare equal and report a pass, which is the exact
# false negative this comparison exists to avoid.
REQUIRED = {
    '[Content_Types].xml',
    '_rels_.rels',
    'xl_workbook.xml',
    'xl_styles.xml',
    'xl__rels_workbook.xml.rels',
    'xl_worksheets_sheet1.xml',
}

names_a, names_b = sorted(os.listdir(a)), sorted(os.listdir(b))
if names_a != names_b:
    print('PART LISTS DIFFER'); print(set(names_a) ^ set(names_b)); sys.exit(1)

missing = REQUIRED.difference(names_a)
if missing:
    print(f'NOT A SAVED WORKBOOK: {len(names_a)} parts, missing {sorted(missing)}')
    print('Nothing was compared. Check the generator ran.')
    sys.exit(2)

differing = [n for n in names_a if norm(os.path.join(a, n)) != norm(os.path.join(b, n))]

for n in differing:
    print('DIFFERS:', n)

print(f'{len(names_a)} parts compared, {len(differing)} differing')
sys.exit(1 if differing else 0)
