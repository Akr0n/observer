"""Generates the Observer icon.

No dependencies: it draws by hand and compresses with zlib, which is in the standard library.
The script is in the repository, not just the binary, so that the icon can be reviewed and
regenerated - a .ico committed on its own is an opaque file whose origin nobody remembers.

    python tools/generate_icon.py

Writes src/Observer.App/Assets/observer.ico and observer.png.
"""

import os
import struct
import zlib

# Dark slate and green: green says "the machine is healthy", and it is the only colour that
# stays distinguishable from everything else on the taskbar at sixteen pixels.
BACKGROUND = (30, 36, 48, 255)
TRACE = (74, 222, 128, 255)

# The heartbeat trace, in normalized coordinates. Three segments would be unreadable at
# sixteen pixels; these six keep their shape even there.
POINTS = [
    (0.13, 0.56), (0.33, 0.56), (0.41, 0.28),
    (0.53, 0.78), (0.63, 0.42), (0.71, 0.56), (0.87, 0.56),
]

THICKNESS = 0.085
RADIUS = 0.22
SAMPLES = 4


def _inside_rounded_rectangle(x, y, radius):
    """Whether the point lies inside the square with rounded corners."""
    dx = max(radius - x, 0.0, x - (1.0 - radius))
    dy = max(radius - y, 0.0, y - (1.0 - radius))

    return dx * dx + dy * dy <= radius * radius


def _distance_to_segment(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    length = vx * vx + vy * vy

    if length == 0.0:
        return ((px - ax) ** 2 + (py - ay) ** 2) ** 0.5

    t = max(0.0, min(1.0, ((px - ax) * vx + (py - ay) * vy) / length))

    return ((px - (ax + t * vx)) ** 2 + (py - (ay + t * vy)) ** 2) ** 0.5


def _distance_to_trace(x, y):
    return min(
        _distance_to_segment(x, y, POINTS[i][0], POINTS[i][1], POINTS[i + 1][0], POINTS[i + 1][1])
        for i in range(len(POINTS) - 1)
    )


def draw(size):
    """Draws the icon at a given size, supersampled for soft edges."""
    large = size * SAMPLES
    pixels = bytearray(large * large * 4)

    for row in range(large):
        y = (row + 0.5) / large

        for column in range(large):
            x = (column + 0.5) / large
            i = (row * large + column) * 4

            if not _inside_rounded_rectangle(x, y, RADIUS):
                continue

            colour = TRACE if _distance_to_trace(x, y) <= THICKNESS / 2 else BACKGROUND
            pixels[i:i + 4] = bytes(colour)

    # Box-filter downsampling: this is what softens both the edge of the square and the trace,
    # without having to write real antialiasing.
    result = bytearray()

    for row in range(size):
        result.append(0)  # PNG filter type "None", once per row

        for column in range(size):
            sums = [0, 0, 0, 0]

            for dy in range(SAMPLES):
                for dx in range(SAMPLES):
                    i = (((row * SAMPLES + dy) * large) + (column * SAMPLES + dx)) * 4

                    for channel in range(4):
                        sums[channel] += pixels[i + channel]

            result.extend(value // (SAMPLES * SAMPLES) for value in sums)

    return bytes(result)


def _chunk(name, data):
    body = name + data

    return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body))


def png(size, rows):
    header = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)

    return (
        b'\x89PNG\r\n\x1a\n'
        + _chunk(b'IHDR', header)
        + _chunk(b'IDAT', zlib.compress(rows, 9))
        + _chunk(b'IEND', b'')
    )


def ico(images):
    """Packs the PNGs into a .ico. Windows accepts PNG inside ICO from Vista onwards."""
    count = len(images)
    entries = b''
    data = b''
    offset = 6 + 16 * count

    for size, content in images:
        # 256 is written as 0: the field is a single byte.
        entries += struct.pack(
            '<BBBBHHII',
            0 if size == 256 else size,
            0 if size == 256 else size,
            0, 0, 1, 32, len(content), offset)
        data += content
        offset += len(content)

    return struct.pack('<HHH', 0, 1, count) + entries + data


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, 'src', 'Observer.App', 'Assets')
    os.makedirs(assets, exist_ok=True)

    images = []

    for size in (16, 32, 48, 64, 128, 256):
        print('drawing %dx%d...' % (size, size))
        images.append((size, png(size, draw(size))))

    ico_path = os.path.join(assets, 'observer.ico')

    with open(ico_path, 'wb') as file:
        file.write(ico(images))

    png_path = os.path.join(assets, 'observer.png')

    with open(png_path, 'wb') as file:
        file.write(images[-1][1])

    print('wrote %s (%d bytes) and %s (%d bytes)' % (
        ico_path, os.path.getsize(ico_path),
        png_path, os.path.getsize(png_path)))


if __name__ == '__main__':
    main()
