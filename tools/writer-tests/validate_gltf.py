#!/usr/bin/env python3
"""Validate the files WriterTests produced.

Checks the GLB container by hand rather than trusting a library: header length,
chunk types and 4-byte padding, bufferView and accessor alignment, declared
POSITION min/max against the real data, and index range. Then reconstructs the
test cube from the buffers and verifies it is closed, unit-volume and wound
counter-clockwise — which is what actually catches a transform or winding
regression, as opposed to merely confirming the JSON is well formed.
"""

import json
import os
import struct
import sys

COMPONENT = {5120: ('b', 1), 5121: ('B', 1), 5122: ('h', 2),
             5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4)}
COMPONENTS_PER = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}

errors = []
notes = []


def fail(message):
    errors.append(message)


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0]]


def sub(a, b):
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def read_accessor(gltf, binary, index):
    accessor = gltf['accessors'][index]
    view = gltf['bufferViews'][accessor['bufferView']]
    fmt, size = COMPONENT[accessor['componentType']]
    count = COMPONENTS_PER[accessor['type']]

    if view.get('byteOffset', 0) % 4:
        fail("bufferView %d byteOffset is not 4-byte aligned" % accessor['bufferView'])

    base = view.get('byteOffset', 0) + accessor.get('byteOffset', 0)
    if base % size:
        fail("accessor %d offset %d is not aligned to its %d-byte component"
             % (index, base, size))

    if base + accessor['count'] * count * size > len(binary):
        fail("accessor %d reads past the end of the buffer" % index)
        return []

    values = struct.unpack_from('<' + fmt * (accessor['count'] * count), binary, base)
    return [values[i * count:(i + 1) * count] for i in range(accessor['count'])]


def check_glb(path):
    data = open(path, 'rb').read()

    magic, version, total = struct.unpack_from('<III', data, 0)
    if magic != 0x46546C67:
        fail("not a GLB: bad magic")
        return None, None
    if version != 2:
        fail("GLB container version %d, expected 2" % version)
    if total != len(data):
        fail("header length %d does not match file size %d" % (total, len(data)))
    else:
        notes.append("GLB header length matches file size (%d bytes)" % total)

    offset = 12
    chunks = []
    while offset < len(data):
        length, kind = struct.unpack_from('<II', data, offset)
        if length % 4:
            fail("chunk 0x%x length %d is not 4-byte aligned" % (kind, length))
        chunks.append((kind, offset + 8, length))
        offset += 8 + length
    if offset != len(data):
        fail("chunk walk overran the file")

    if len(chunks) < 2 or chunks[0][0] != 0x4E4F534A or chunks[1][0] != 0x004E4942:
        fail("expected a JSON chunk followed by a BIN chunk")
        return None, None
    notes.append("JSON and BIN chunks present and 4-byte aligned")

    gltf = json.loads(data[chunks[0][1]:chunks[0][1] + chunks[0][2]].decode('utf8'))
    binary = data[chunks[1][1]:chunks[1][1] + chunks[1][2]]

    declared = gltf['buffers'][0]['byteLength']
    if declared > len(binary):
        fail("buffer byteLength %d exceeds the BIN chunk (%d)" % (declared, len(binary)))
    elif len(binary) - declared > 3:
        fail("BIN chunk padded by %d bytes, expected at most 3" % (len(binary) - declared))
    else:
        notes.append("buffer byteLength %d, BIN chunk %d" % (declared, len(binary)))

    return gltf, binary


def check_cube(gltf, binary):
    primitive = gltf['meshes'][0]['primitives'][0]
    positions = read_accessor(gltf, binary, primitive['attributes']['POSITION'])
    normals = read_accessor(gltf, binary, primitive['attributes']['NORMAL'])
    indices = [i[0] for i in read_accessor(gltf, binary, primitive['indices'])]

    if not positions or not indices:
        return

    notes.append("POSITION %d, NORMAL %d, indices %d, mode %s"
                 % (len(positions), len(normals), len(indices), primitive.get('mode')))

    # The tessellator emits 36 loose vertices for a cube; welding on position
    # alone would give 8 and destroy the creases. 24 is position+normal welding.
    if len(positions) != 24:
        fail("expected 24 welded vertices for the test cube, got %d" % len(positions))

    accessor = gltf['accessors'][primitive['attributes']['POSITION']]
    actual_min = [min(p[c] for p in positions) for c in range(3)]
    actual_max = [max(p[c] for p in positions) for c in range(3)]
    if ([round(v, 5) for v in accessor['min']] != [round(v, 5) for v in actual_min] or
            [round(v, 5) for v in accessor['max']] != [round(v, 5) for v in actual_max]):
        fail("POSITION min/max declared %s/%s but data is %s/%s"
             % (accessor['min'], accessor['max'], actual_min, actual_max))
    else:
        notes.append("POSITION min/max correct: %s .. %s" % (actual_min, actual_max))

    if max(indices) >= len(positions):
        fail("index %d is out of range for %d vertices" % (max(indices), len(positions)))

    triangles = [(positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]])
                 for i in range(0, len(indices), 3)]

    volume = sum(dot(t[0], cross(t[1], t[2])) / 6.0 for t in triangles)
    if abs(abs(volume) - 1.0) > 1e-4:
        fail("reconstructed signed volume %f, expected +/-1 (not a closed unit cube)" % volume)
    else:
        notes.append("closed unit cube reconstructed, signed volume %+.6f" % volume)

    centre = [0.5, 0.5, 0.5]
    inward = 0
    for triangle in triangles:
        normal = cross(sub(triangle[1], triangle[0]), sub(triangle[2], triangle[0]))
        middle = [(triangle[0][k] + triangle[1][k] + triangle[2][k]) / 3.0 for k in range(3)]
        if dot(normal, sub(middle, centre)) <= 0:
            inward += 1
    if inward:
        fail("%d of %d triangles are wound inward; glTF front faces must be CCW"
             % (inward, len(triangles)))
    else:
        notes.append("all %d triangles wound counter-clockwise" % len(triangles))

    bad_normals = sum(1 for n in normals
                      if abs(abs(n[0]) + abs(n[1]) + abs(n[2]) - 1.0) > 1e-4)
    if bad_normals:
        fail("%d normals are not unit length" % bad_normals)
    else:
        notes.append("all normals unit length")

    extras = gltf.get('asset', {}).get('extras', {})
    missing = [k for k in ('navex:appliedOffset', 'navex:sourceUnits',
                           'navex:targetUnits', 'navex:upAxis') if k not in extras]
    if missing:
        fail("provenance keys missing from asset.extras: %s" % ", ".join(missing))
    else:
        notes.append("provenance recorded: offset %s, %s -> %s, up %s"
                     % (extras['navex:appliedOffset'], extras['navex:sourceUnits'],
                        extras['navex:targetUnits'], extras['navex:upAxis']))


def check_gltf_pair(directory):
    gltf = json.load(open(os.path.join(directory, 'out.gltf')))
    uri = gltf['buffers'][0]['uri']
    binary_path = os.path.join(directory, uri)
    if not os.path.exists(binary_path):
        fail("out.gltf references %s, which does not exist" % uri)
    elif os.path.getsize(binary_path) < gltf['buffers'][0]['byteLength']:
        fail("%s is shorter than the declared byteLength" % uri)
    else:
        notes.append("out.gltf references %s (%d bytes)" % (uri, os.path.getsize(binary_path)))


def check_obj(directory):
    vertices = []
    faces = []
    for line in open(os.path.join(directory, 'out.obj')):
        parts = line.split()
        if not parts:
            continue
        if parts[0] == 'v':
            vertices.append([float(x) for x in parts[1:4]])
        elif parts[0] == 'f':
            faces.append([int(t.split('//')[0]) for t in parts[1:4]])

    if not faces:
        fail("out.obj contains no faces")
        return
    if min(min(f) for f in faces) < 1:
        fail("out.obj indices are not 1-based")

    volume = sum(dot(vertices[f[0] - 1], cross(vertices[f[1] - 1], vertices[f[2] - 1])) / 6.0
                 for f in faces)
    if abs(abs(volume) - 1.0) > 1e-4:
        fail("out.obj reconstructed volume %f, expected +/-1" % volume)
    else:
        notes.append("out.obj: %d vertices, %d faces, signed volume %+.6f"
                     % (len(vertices), len(faces), volume))

    if not os.path.exists(os.path.join(directory, 'out.mtl')):
        fail("out.mtl was not written")


def main():
    directory = sys.argv[1] if len(sys.argv) > 1 else '.'

    gltf, binary = check_glb(os.path.join(directory, 'out.glb'))
    if gltf is not None:
        check_cube(gltf, binary)
    check_gltf_pair(directory)
    check_obj(directory)

    for note in notes:
        print("  ok   " + note)
    for error in errors:
        print("  FAIL " + error)

    if errors:
        print("\n%d check(s) failed." % len(errors))
        return 1
    print("\nAll writer checks passed.")
    return 0


if __name__ == '__main__':
    sys.exit(main())
