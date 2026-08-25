"""Reduce the retail Terminal acoustics authoring to the exact baked graph.

The retail sound scene contains later/copied authoring objects whose serialized
IDs collide with the 76-room/220-portal audio bake.  The bake itself gives every
portal's true room endpoints through its one-portal routes.  A component belongs
to the bake iff its serialized portal ID and its two serialized room IDs agree
with that endpoint record.  This is an exact selection rule, not a name/position
heuristic.
"""

from __future__ import annotations

import argparse
import collections
import copy
import json
from pathlib import Path
import struct


def read_exact(stream, fmt: str):
    size = struct.calcsize(fmt)
    data = stream.read(size)
    if len(data) != size:
        raise EOFError(f"wanted {size} bytes at {stream.tell() - len(data)}")
    values = struct.unpack(fmt, data)
    return values[0] if len(values) == 1 else values


def read_baked_graph(path: Path):
    endpoints: dict[int, set[tuple[int, int]]] = collections.defaultdict(set)
    rooms: set[int] = set()
    portals: set[int] = set()
    with path.open("rb") as stream:
        pair_count = read_exact(stream, "<i")
        for _ in range(pair_count):
            _key = read_exact(stream, "<I")
            _record_id = read_exact(stream, "<I")
            first, second = read_exact(stream, "<hh")
            rooms.update((first, second))
            route_count = read_exact(stream, "<i")
            for _ in range(route_count):
                portal_count = read_exact(stream, "<i")
                route_portals = [read_exact(stream, "<h") for _ in range(portal_count)]
                portals.update(route_portals)
                read_exact(stream, "<ff")
                if portal_count == 1:
                    endpoints[route_portals[0]].add(tuple(sorted((first, second))))
            read_exact(stream, "<iB")
    return pair_count, rooms, portals, endpoints


def refs(tokens) -> list[int]:
    return [token["ref"] for token in tokens if isinstance(token, dict) and "ref" in token]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--bake", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    document = json.loads(args.input.read_text(encoding="utf-8"))
    repaired = copy.deepcopy(document)
    sound = repaired["scenes"]["Terminal_Sound"]
    pair_count, baked_room_ids, baked_portal_ids, baked_endpoints = read_baked_graph(args.bake)

    room_rows = sound["SpatialAudioRoom"]
    room_by_path = {row["path_id"]: row for row in room_rows}
    room_id_by_path = {
        path_id: row["fields"]["_iD"] for path_id, row in room_by_path.items()
    }

    portal_classes = ("SpatialAudioPortal", "UniversalTriggerSpatialAudioPortal")
    selected_portals: list[tuple[str, dict]] = []
    for class_name in portal_classes:
        for row in sound.get(class_name, []):
            fields = row["fields"]
            portal_id = fields["_iD"]
            room_paths = refs(fields.get("_connectedRooms", []))
            if len(room_paths) != 2 or any(path not in room_id_by_path for path in room_paths):
                continue
            endpoint = tuple(sorted(room_id_by_path[path] for path in room_paths))
            if endpoint in baked_endpoints.get(portal_id, set()):
                selected_portals.append((class_name, row))

    portal_ids = [row["fields"]["_iD"] for _, row in selected_portals]
    assert len(selected_portals) == len(baked_portal_ids) == 220
    assert len(set(portal_ids)) == len(portal_ids)
    assert set(portal_ids) == baked_portal_ids

    selected_room_paths = {
        path
        for _, row in selected_portals
        for path in refs(row["fields"]["_connectedRooms"])
    }
    selected_rooms = [room_by_path[path] for path in selected_room_paths]
    selected_room_ids = [row["fields"]["_iD"] for row in selected_rooms]
    assert len(selected_rooms) == len(baked_room_ids) == 76
    assert len(set(selected_room_ids)) == len(selected_room_ids)
    assert set(selected_room_ids) == baked_room_ids

    # Rebuild room adjacency solely from the selected portal endpoints.  This
    # removes stale references to copied/non-baked portals and makes both sides
    # of every edge exactly symmetric.
    adjacency: dict[int, dict[int, list[int]]] = collections.defaultdict(
        lambda: collections.defaultdict(list)
    )
    for _, portal in selected_portals:
        portal_path = portal["path_id"]
        front, back = refs(portal["fields"]["_connectedRooms"])
        adjacency[front][back].append(portal_path)
        adjacency[back][front].append(portal_path)

    for room in selected_rooms:
        room_path = room["path_id"]
        room["fields"]["roomConnections"] = [
            {
                "connectedRoom": {"ref": other_path},
                "connectingPortals": [
                    {"ref": portal_path}
                    for portal_path in sorted(portal_paths)
                ],
            }
            for other_path, portal_paths in sorted(adjacency[room_path].items())
        ]

    selected_portal_paths = {row["path_id"] for _, row in selected_portals}
    for class_name in portal_classes:
        sound[class_name] = [
            row for row in sound.get(class_name, []) if row["path_id"] in selected_portal_paths
        ]
    sound["SpatialAudioRoom"] = sorted(
        selected_rooms, key=lambda row: row["fields"]["_iD"]
    )

    selected_area_paths = {
        path
        for room in selected_rooms
        for path in refs(room["fields"].get("Areas", []))
    }
    all_area_paths = {row["path_id"] for row in sound.get("AudioTriggerArea", [])}
    missing_areas = selected_area_paths - all_area_paths
    assert not missing_areas, f"selected rooms reference missing areas: {sorted(missing_areas)}"
    sound["AudioTriggerArea"] = [
        row for row in sound.get("AudioTriggerArea", []) if row["path_id"] in selected_area_paths
    ]

    repaired["acoustics_repair"] = {
        "method": "portal ID + direct baked-route endpoint identity",
        "room_pairs": pair_count,
        "rooms": len(selected_rooms),
        "portals": len(selected_portals),
        "trigger_areas": len(selected_area_paths),
    }
    args.output.write_text(json.dumps(repaired, indent=1) + "\n", encoding="utf-8")
    print(
        f"wrote {args.output}: {len(selected_rooms)} rooms, "
        f"{len(selected_portals)} portals, {len(selected_area_paths)} trigger areas"
    )


if __name__ == "__main__":
    main()
