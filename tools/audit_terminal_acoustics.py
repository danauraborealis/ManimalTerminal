"""Audit retail Terminal spatial-audio components against the extracted sidecar.

This intentionally reuses the already decoded IDs in the sidecar and reads only
Unity's built-in MonoBehaviour/GameObject headers from level636.  That lets us
test whether duplicate baked IDs came from disabled or inactive authoring copies
without depending on the hand-patched retail typetree a second time.
"""

from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path
import struct

import UnityPy


TYPES = ("SpatialAudioRoom", "SpatialAudioPortal", "UniversalTriggerSpatialAudioPortal")


def read_exact(stream, fmt: str):
    size = struct.calcsize(fmt)
    data = stream.read(size)
    if len(data) != size:
        raise EOFError(f"wanted {size} bytes at {stream.tell() - len(data)}")
    values = struct.unpack(fmt, data)
    return values[0] if len(values) == 1 else values


def read_baked_graph(path: Path):
    """Return portal ID -> endpoint room-pair candidates from direct baked routes."""
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
                read_exact(stream, "<ff")  # heuristic cost, traverse distance
                if portal_count == 1:
                    endpoints[route_portals[0]].add(tuple(sorted((first, second))))
            read_exact(stream, "<iB")  # total portals, shortest route length
    return pair_count, rooms, portals, endpoints


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sidecar", type=Path, required=True)
    parser.add_argument("--levels", type=Path, required=True)
    parser.add_argument("--bake", type=Path, required=True)
    args = parser.parse_args()

    sidecar = json.loads(args.sidecar.read_text(encoding="utf-8"))
    sound = sidecar["scenes"]["Terminal_Sound"]
    pair_count, baked_rooms, baked_portals, baked_endpoints = read_baked_graph(args.bake)
    print(
        f"Bake: {pair_count} room pairs, rooms={min(baked_rooms)}..{max(baked_rooms)} "
        f"({len(baked_rooms)}), portals={min(baked_portals)}..{max(baked_portals)} "
        f"({len(baked_portals)}), direct endpoint mappings={len(baked_endpoints)}"
    )

    env = UnityPy.load(
        str(args.levels / "globalgamemanagers.assets"),
        str(args.levels / "level636"),
    )
    serialized = next(f for k, f in env.files.items() if str(k).endswith("level636"))

    component_state: dict[int, tuple[bool, int]] = {}
    game_object_state: dict[int, bool] = {}
    for obj in serialized.objects.values():
        try:
            if obj.type.name == "MonoBehaviour":
                mb = obj.read(check_read=False)
                component_state[obj.path_id] = (bool(mb.m_Enabled), mb.m_GameObject.path_id)
            elif obj.type.name == "GameObject":
                go = obj.read()
                game_object_state[obj.path_id] = bool(go.m_IsActive)
        except Exception:
            continue

    for class_name in TYPES:
        groups: dict[int, list[dict]] = collections.defaultdict(list)
        for row in sound.get(class_name, []):
            fields = row.get("fields", {})
            baked_id = fields.get("_iD")
            if isinstance(baked_id, int):
                groups[baked_id].append(row)

        print(f"\n{class_name}: {sum(map(len, groups.values()))} rows, {len(groups)} IDs")
        enabled_rows = 0
        active_rows = 0
        unique_enabled = 0
        for baked_id, rows in sorted(groups.items()):
            enabled = []
            for row in rows:
                state = component_state.get(row["path_id"], (False, row.get("go_id", 0)))
                is_enabled, go_id = state
                is_active = game_object_state.get(go_id, False)
                enabled_rows += int(is_enabled)
                active_rows += int(is_active)
                if is_enabled and is_active:
                    enabled.append(row)
            unique_enabled += int(len(enabled) == 1)
            if len(rows) > 1:
                print(f"  ID {baked_id}: {len(rows)} candidates; enabled+active={len(enabled)}")
                for row in rows:
                    is_enabled, go_id = component_state.get(
                        row["path_id"], (False, row.get("go_id", 0))
                    )
                    print(
                        "    "
                        f"pid={row['path_id']} enabled={int(is_enabled)} "
                        f"active={int(game_object_state.get(go_id, False))} "
                        f"go={row.get('go')}"
                    )

        print(
            f"  enabled rows={enabled_rows}; active GO rows={active_rows}; "
            f"IDs with exactly one enabled+active candidate={unique_enabled}/{len(groups)}"
        )

    room_id_by_path = {
        row["path_id"]: row.get("fields", {}).get("_iD")
        for row in sound.get("SpatialAudioRoom", [])
    }
    portal_rows = sound.get("SpatialAudioPortal", []) + sound.get(
        "UniversalTriggerSpatialAudioPortal", []
    )
    consistent = []
    inconsistent = []
    for row in portal_rows:
        fields = row.get("fields", {})
        portal_id = fields.get("_iD")
        refs = [r.get("ref") for r in fields.get("_connectedRooms", [])]
        endpoint_ids = tuple(sorted(room_id_by_path.get(ref) for ref in refs)) if len(refs) == 2 else ()
        record = (portal_id, endpoint_ids, row["path_id"], row.get("go"))
        if portal_id in baked_endpoints and endpoint_ids in baked_endpoints[portal_id]:
            consistent.append(record)
        else:
            inconsistent.append(record)

    print(
        f"\nSerialized-ID topology check: {len(consistent)} portals agree with the bake; "
        f"{len(inconsistent)} disagree"
    )
    for record in inconsistent[:30]:
        portal_id, endpoint_ids, path_id, go = record
        print(
            f"  mismatch portal={portal_id} rooms={endpoint_ids} pid={path_id} go={go}; "
            f"bake={sorted(baked_endpoints.get(portal_id, []))}"
        )


if __name__ == "__main__":
    main()
