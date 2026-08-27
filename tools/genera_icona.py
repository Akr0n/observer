"""Genera l'icona di Observer.

Nessuna dipendenza: disegna a mano e comprime con zlib, che sta nella libreria standard.
Lo script sta nel repository invece del solo binario, cosi' l'icona si puo' rivedere e
rifare - un .ico committato e basta e' un file opaco che nessuno sa piu' come e' nato.

    python tools/genera_icona.py

Produce src/Observer.App/Assets/observer.ico e observer.png.
"""

import os
import struct
import zlib

# Ardesia scura e verde: il verde dice "la macchina sta bene", ed e' l'unico colore che a
# sedici pixel resta distinguibile da tutto il resto della barra delle applicazioni.
SFONDO = (30, 36, 48, 255)
TRACCIA = (74, 222, 128, 255)

# Il tracciato del battito, in coordinate normalizzate. Tre segmenti sarebbero illeggibili a
# sedici pixel; questi cinque tengono la forma anche li'.
PUNTI = [
    (0.13, 0.56), (0.33, 0.56), (0.41, 0.28),
    (0.53, 0.78), (0.63, 0.42), (0.71, 0.56), (0.87, 0.56),
]

SPESSORE = 0.085
RAGGIO = 0.22
CAMPIONI = 4


def _dentro_rettangolo_arrotondato(x, y, raggio):
    """Se il punto sta dentro il quadrato con gli angoli smussati."""
    dx = max(raggio - x, 0.0, x - (1.0 - raggio))
    dy = max(raggio - y, 0.0, y - (1.0 - raggio))

    return dx * dx + dy * dy <= raggio * raggio


def _distanza_dal_segmento(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    lunghezza = vx * vx + vy * vy

    if lunghezza == 0.0:
        return ((px - ax) ** 2 + (py - ay) ** 2) ** 0.5

    t = max(0.0, min(1.0, ((px - ax) * vx + (py - ay) * vy) / lunghezza))

    return ((px - (ax + t * vx)) ** 2 + (py - (ay + t * vy)) ** 2) ** 0.5


def _distanza_dalla_traccia(x, y):
    return min(
        _distanza_dal_segmento(x, y, PUNTI[i][0], PUNTI[i][1], PUNTI[i + 1][0], PUNTI[i + 1][1])
        for i in range(len(PUNTI) - 1)
    )


def disegna(lato):
    """Disegna l'icona a un lato dato, con sovracampionamento per i bordi morbidi."""
    grande = lato * CAMPIONI
    pixel = bytearray(grande * grande * 4)

    for riga in range(grande):
        y = (riga + 0.5) / grande

        for colonna in range(grande):
            x = (colonna + 0.5) / grande
            i = (riga * grande + colonna) * 4

            if not _dentro_rettangolo_arrotondato(x, y, RAGGIO):
                continue

            colore = TRACCIA if _distanza_dalla_traccia(x, y) <= SPESSORE / 2 else SFONDO
            pixel[i:i + 4] = bytes(colore)

    # Riduzione a scatola: e' cio' che rende morbidi sia il bordo del quadrato sia la traccia,
    # senza dover scrivere un antialiasing vero.
    finale = bytearray()

    for riga in range(lato):
        finale.append(0)  # filtro PNG "nessuno", una volta per riga

        for colonna in range(lato):
            somma = [0, 0, 0, 0]

            for dy in range(CAMPIONI):
                for dx in range(CAMPIONI):
                    i = (((riga * CAMPIONI + dy) * grande) + (colonna * CAMPIONI + dx)) * 4

                    for canale in range(4):
                        somma[canale] += pixel[i + canale]

            finale.extend(valore // (CAMPIONI * CAMPIONI) for valore in somma)

    return bytes(finale)


def _pezzo(nome, dati):
    corpo = nome + dati

    return struct.pack('>I', len(dati)) + corpo + struct.pack('>I', zlib.crc32(corpo))


def png(lato, righe):
    intestazione = struct.pack('>IIBBBBB', lato, lato, 8, 6, 0, 0, 0)

    return (
        b'\x89PNG\r\n\x1a\n'
        + _pezzo(b'IHDR', intestazione)
        + _pezzo(b'IDAT', zlib.compress(righe, 9))
        + _pezzo(b'IEND', b'')
    )


def ico(immagini):
    """Impacchetta i PNG in un .ico. Windows accetta PNG dentro ICO da Vista in poi."""
    conteggio = len(immagini)
    voci = b''
    dati = b''
    scostamento = 6 + 16 * conteggio

    for lato, contenuto in immagini:
        # 256 si scrive come 0: il campo e' di un byte solo.
        voci += struct.pack(
            '<BBBBHHII',
            0 if lato == 256 else lato,
            0 if lato == 256 else lato,
            0, 0, 1, 32, len(contenuto), scostamento)
        dati += contenuto
        scostamento += len(contenuto)

    return struct.pack('<HHH', 0, 1, conteggio) + voci + dati


def main():
    radice = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(radice, 'src', 'Observer.App', 'Assets')
    os.makedirs(assets, exist_ok=True)

    immagini = []

    for lato in (16, 32, 48, 64, 128, 256):
        print('disegno %dx%d...' % (lato, lato))
        immagini.append((lato, png(lato, disegna(lato))))

    percorso_ico = os.path.join(assets, 'observer.ico')

    with open(percorso_ico, 'wb') as file:
        file.write(ico(immagini))

    percorso_png = os.path.join(assets, 'observer.png')

    with open(percorso_png, 'wb') as file:
        file.write(immagini[-1][1])

    print('scritti %s (%d byte) e %s (%d byte)' % (
        percorso_ico, os.path.getsize(percorso_ico),
        percorso_png, os.path.getsize(percorso_png)))


if __name__ == '__main__':
    main()
