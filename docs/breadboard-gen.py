# -*- coding: utf-8 -*-
F = "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif"
P = 24
COL1_X, ROWA_Y = 316, 186
ROWS_TOP = "ABCDE"
ROWS_BOT = "FGHIJ"
CHANNEL_TOP, CHANNEL_BOT = 294, 350

def cx(c): return COL1_X + (c - 1) * P
def ry(letter):
    if letter in ROWS_TOP: return ROWA_Y + ROWS_TOP.index(letter) * P
    return CHANNEL_BOT + 12 + ROWS_BOT.index(letter) * P

RED, AMBER, DARK, GREY = "#c0392b", "#b07d00", "#2f343a", "#9aa1ab"
NODES = [(5, "GND", DARK), (6, "DQ", AMBER), (7, "VDD", RED)]

o = []
a = o.append
a('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 624" width="760" height="624" role="img" '
  'aria-label="Vue breadboard du montage : fil 3,3 V en colonne 7, GPIO 4 en colonne 6, GND en colonne 5, '
  'resistance de 4,7 kilo-ohms entre colonnes 6 et 7, capteur DS18B20 pattes en colonnes 5, 6 et 7.">')
a('<rect x="1" y="1" width="758" height="622" rx="14" fill="#fdfdfd" stroke="#dfe3e8" stroke-width="2"/>')
a(f'<text x="30" y="40" font-family="{F}" font-size="17" font-weight="600" fill="#1b1f24">Vue breadboard</text>')

# Corps de la platine
a('<rect x="280" y="150" width="444" height="336" rx="10" fill="#f2f3f5" stroke="#c8ccd2" stroke-width="2"/>')
a(f'<rect x="288" y="{CHANNEL_TOP}" width="428" height="{CHANNEL_BOT-CHANNEL_TOP}" fill="#e6e8ec"/>')

# Colonnes utilisées, mises en évidence sur toute la moitié haute
for col, label, color in NODES:
    a(f'<rect x="{cx(col)-10}" y="176" width="20" height="116" rx="10" fill="{color}" fill-opacity="0.15"/>')
    a(f'<text x="{cx(col)}" y="171" text-anchor="middle" font-family="{F}" font-size="10.5" '
      f'font-weight="700" fill="{color}">{label}</text>')

# Numéros de colonne et lettres de rangée
for c in range(1, 18):
    a(f'<text x="{cx(c)}" y="140" text-anchor="middle" font-family="{F}" font-size="9" fill="#8a919b">{c}</text>')
for letter in ROWS_TOP + ROWS_BOT:
    a(f'<text x="712" y="{ry(letter)+4}" text-anchor="middle" font-family="{F}" font-size="9" fill="#8a919b">{letter}</text>')

# Trous
for c in range(1, 18):
    for letter in ROWS_TOP + ROWS_BOT:
        a(f'<circle cx="{cx(c)}" cy="{ry(letter)}" r="3.6" fill="#c2c7ce"/>')

# Fils venant du Raspberry Pi
WIRES = [("Broche 6 — GND", DARK, 5, "A"), ("Broche 7 — GPIO 4", AMBER, 6, "B"), ("Broche 1 — 3,3 V", RED, 7, "C")]
for label, color, col, row in WIRES:
    y = ry(row)
    a(f'<text x="262" y="{y+5}" text-anchor="end" font-family="{F}" font-size="13.5" '
      f'font-weight="600" fill="{color}">{label}</text>')
    a(f'<line x1="272" y1="{y}" x2="{cx(col)}" y2="{y}" stroke="{color}" stroke-width="3.5" stroke-linecap="round"/>')
    a(f'<circle cx="{cx(col)}" cy="{y}" r="5.5" fill="{color}"/>')

# Résistance, en pont entre la colonne DQ et la colonne 3,3 V
yd = ry("D")
a(f'<line x1="{cx(6)}" y1="{yd}" x2="{cx(6)+2}" y2="{yd-12}" stroke="{GREY}" stroke-width="2.5"/>')
a(f'<line x1="{cx(7)}" y1="{yd}" x2="{cx(7)-2}" y2="{yd-12}" stroke="{GREY}" stroke-width="2.5"/>')
a(f'<rect x="{cx(6)-3}" y="{yd-25}" width="{P+6}" height="14" rx="3" fill="#ffffff" stroke="#1b1f24" stroke-width="2"/>')
a(f'<circle cx="{cx(6)}" cy="{yd}" r="5" fill="{AMBER}"/><circle cx="{cx(7)}" cy="{yd}" r="5" fill="{RED}"/>')
a(f'<text x="{cx(8)+14}" y="{yd-18}" font-family="{F}" font-size="13" font-weight="600" fill="#1b1f24">4,7 kΩ</text>')

# Capteur, pattes en rangée E, corps posé dans le canal central
ye = ry("E")
for col in (5, 6, 7):
    a(f'<line x1="{cx(col)}" y1="{ye}" x2="{cx(col)}" y2="298" stroke="{GREY}" stroke-width="3"/>')
    a(f'<circle cx="{cx(col)}" cy="{ye}" r="5" fill="{GREY}"/>')
a(f'<rect x="{cx(5)-16}" y="298" width="{2*P+32}" height="44" rx="10" fill="#d8dbe0" stroke="{GREY}" stroke-width="2"/>')
a(f'<text x="{cx(6)}" y="326" text-anchor="middle" font-family="{F}" font-size="12" '
  f'font-weight="700" fill="#1b1f24">DS18B20</text>')

# Légende
notes = [
    ("Chaque colonne de 5 trous est reliée en interne. Les deux moitiés sont séparées par le canal central.", "#1b1f24"),
    ("La résistance relie la colonne 6 (DQ) à la colonne 7 (3,3 V) — jamais à la colonne 5 (GND).", "#c0392b"),
    ("Cette mini-platine n'a pas de rails d'alimentation : le fil rouge n'alimente que la colonne 7.", "#1b1f24"),
    ("Capteur : méplat tourné vers vous — GND à gauche (5), DQ au centre (6), VDD à droite (7).", "#1b1f24"),
]
for i, (text, color) in enumerate(notes):
    a(f'<text x="30" y="{522 + i*24}" font-family="{F}" font-size="13.5" fill="{color}">{text}</text>')

a('</svg>')
open("/home/user/tempi/docs/breadboard.svg", "w").write("\n".join(o) + "\n")
print("écrit")
