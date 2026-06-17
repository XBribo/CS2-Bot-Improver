#!/usr/bin/env python3
from __future__ import annotations

import argparse
import brotli
import concurrent.futures
import datetime as dt
from dataclasses import dataclass
import hashlib
import json
import math
import os
import re
import shutil
import signal
import struct
import subprocess
import time
import tempfile
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import urljoin

import requests
from demoparser2 import DemoParser


DEFAULT_RESULTS_URL = "https://www.hltv.org/results?maps=de_dust2"
DEFAULT_USER_AGENT = (
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/122.0 Safari/537.36"
)
MOVETYPE_LADDER = 9


@dataclass(frozen=True)
class Cs2RecBundleEntry:
    key: str
    payload: bytes
    weapon_defs: tuple[int, ...]

ESSENTIAL_TICK_PROPS = [
    "X",
    "Y",
    "Z",
    "pitch",
    "yaw",
    "buttons",
    "team_num",
    "player_name",
    "player_steamid",
    "balance",
    "inventory",
    "active_weapon_name",
    "total_rounds_played",
    "is_alive",
    "current_equip_value",
    "round_start_equip_value",
    "armor_value",
    "has_helmet",
    "has_defuser",
]

OPTIONAL_TICK_PROPS = [
    "team_clan_name",
    "round_in_progress",
    "is_freeze_period",
    "velocity_X",
    "velocity_Y",
    "velocity_Z",
    "item_def_idx",
    "inventory_as_ids",
    "move_type",
    "CCSPlayerPawn.m_fFlags",
    "duck_amount",
    "duck_speed",
    "ducked",
    "ducking",
    "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
    "usercmd_viewangle_x",
    "usercmd_viewangle_y",
    "usercmd_buttonstate_1",
    "usercmd_buttonstate_2",
    "usercmd_buttonstate_3",
    "usercmd_forward_move",
    "usercmd_left_move",
    "usercmd_weapon_select",
    "usercmd_left_hand_desired",
    "usercmd_attack1_start_history_index",
    "usercmd_attack2_start_history_index",
    "usercmd_input_history",
    "usercmd_subtick_moves",
]

RECORDER_TICK_PROPS = [
    "X",
    "Y",
    "Z",
    "velocity_X",
    "velocity_Y",
    "velocity_Z",
    "CCSPlayerPawn.m_angEyeAngles",
    "CCSPlayerPawn.m_fFlags",
    "CCSPlayerPawn.m_MoveType",
    "CCSPlayerPawn.m_nActualMoveType",
    "usercmd_buttonstate_1",
    "usercmd_buttonstate_2",
    "usercmd_buttonstate_3",
    "usercmd_weapon_select",
    "usercmd_left_hand_desired",
    "usercmd_attack1_start_history_index",
    "usercmd_attack2_start_history_index",
    "duck_amount",
    "duck_speed",
    "ducked",
    "ducking",
    "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
    "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal",
    "usercmd_viewangle_x",
    "usercmd_viewangle_y",
    "usercmd_input_history",
    "usercmd_subtick_moves",
    "item_def_idx",
]

WEAPON_ALIASES = {
    "ak-47": "weapon_ak47",
    "aug": "weapon_aug",
    "awp": "weapon_awp",
    "cz75-auto": "weapon_cz75a",
    "cz75a": "weapon_cz75a",
    "desert eagle": "weapon_deagle",
    "dual berettas": "weapon_elite",
    "famas": "weapon_famas",
    "five-seven": "weapon_fiveseven",
    "flashbang": "weapon_flashbang",
    "g3sg1": "weapon_g3sg1",
    "galil ar": "weapon_galilar",
    "glock-18": "weapon_glock",
    "glock": "weapon_glock",
    "he grenade": "weapon_hegrenade",
    "high explosive grenade": "weapon_hegrenade",
    "incendiary grenade": "weapon_incgrenade",
    "m249": "weapon_m249",
    "m4a1-s": "weapon_m4a1_silencer",
    "m4a4": "weapon_m4a1",
    "mac-10": "weapon_mac10",
    "mag-7": "weapon_mag7",
    "molotov": "weapon_molotov",
    "mp5-sd": "weapon_mp5sd",
    "mp7": "weapon_mp7",
    "mp9": "weapon_mp9",
    "negev": "weapon_negev",
    "nova": "weapon_nova",
    "p2000": "weapon_hkp2000",
    "p250": "weapon_p250",
    "p90": "weapon_p90",
    "r8 revolver": "weapon_revolver",
    "revolver": "weapon_revolver",
    "sawed-off": "weapon_sawedoff",
    "scar-20": "weapon_scar20",
    "sg 553": "weapon_sg556",
    "sg556": "weapon_sg556",
    "smoke grenade": "weapon_smokegrenade",
    "ssg 08": "weapon_ssg08",
    "ssg08": "weapon_ssg08",
    "tec-9": "weapon_tec9",
    "ump-45": "weapon_ump45",
    "usp-s": "weapon_usp_silencer",
    "xm1014": "weapon_xm1014",
}

PROJECTILE_TO_WEAPON = {
    "CSmokeGrenade": "weapon_smokegrenade",
    "CSmokeGrenadeProjectile": "weapon_smokegrenade",
    "SmokeGrenade": "weapon_smokegrenade",
    "CFlashbang": "weapon_flashbang",
    "CFlashbangProjectile": "weapon_flashbang",
    "Flashbang": "weapon_flashbang",
    "CHEGrenade": "weapon_hegrenade",
    "CHEGrenadeProjectile": "weapon_hegrenade",
    "HeGrenade": "weapon_hegrenade",
    "CMolotovGrenade": "weapon_molotov",
    "CMolotovProjectile": "weapon_molotov",
    "Molotov": "weapon_molotov",
    "CIncendiaryGrenade": "weapon_incgrenade",
    "CIncendiaryGrenadeProjectile": "weapon_incgrenade",
    "IncendiaryGrenade": "weapon_incgrenade",
    "CDecoyGrenade": "weapon_decoy",
    "CDecoyProjectile": "weapon_decoy",
    "DecoyGrenade": "weapon_decoy",
}

SUPPORTED_SOURCE_SUFFIXES = {".dem", ".rar", ".zip", ".7z"}

MAP_ALIASES = {
    "de_dust2": {"de_dust2", "de_dust_2", "dust2"},
    "de_dust_2": {"de_dust2", "de_dust_2", "dust2"},
    "de_inferno": {"de_inferno", "inferno"},
    "de_mirage": {"de_mirage", "mirage"},
    "de_nuke": {"de_nuke", "nuke"},
    "de_overpass": {"de_overpass", "overpass"},
    "de_ancient": {"de_ancient", "ancient"},
    "de_anubis": {"de_anubis", "anubis"},
    "de_vertigo": {"de_vertigo", "vertigo"},
    "de_train": {"de_train", "train"},
}

PRIMARY_WEAPONS = {
    "weapon_ak47",
    "weapon_aug",
    "weapon_awp",
    "weapon_famas",
    "weapon_g3sg1",
    "weapon_galilar",
    "weapon_m4a1",
    "weapon_m4a1_silencer",
    "weapon_sg556",
    "weapon_ssg08",
    "weapon_scar20",
    "weapon_mac10",
    "weapon_mp5sd",
    "weapon_mp7",
    "weapon_mp9",
    "weapon_bizon",
    "weapon_p90",
    "weapon_ump45",
    "weapon_mag7",
    "weapon_nova",
    "weapon_sawedoff",
    "weapon_xm1014",
    "weapon_m249",
    "weapon_negev",
}

SECONDARY_WEAPONS = {
    "weapon_glock",
    "weapon_hkp2000",
    "weapon_usp_silencer",
    "weapon_elite",
    "weapon_p250",
    "weapon_tec9",
    "weapon_fiveseven",
    "weapon_deagle",
    "weapon_cz75a",
    "weapon_revolver",
}

UTILITY_ITEMS = {
    "weapon_flashbang",
    "weapon_hegrenade",
    "weapon_smokegrenade",
    "weapon_molotov",
    "weapon_incgrenade",
    "weapon_decoy",
    "weapon_taser",
}

ITEM_PRICES = {
    "item_kevlar": 650,
    "item_assaultsuit": 1000,
    "item_defuser": 400,
    "weapon_taser": 200,
    "weapon_elite": 300,
    "weapon_p250": 300,
    "weapon_tec9": 500,
    "weapon_fiveseven": 500,
    "weapon_deagle": 700,
    "weapon_cz75a": 500,
    "weapon_revolver": 600,
    "weapon_mac10": 1050,
    "weapon_mp9": 1250,
    "weapon_mp7": 1500,
    "weapon_mp5sd": 1500,
    "weapon_ump45": 1200,
    "weapon_bizon": 1400,
    "weapon_p90": 2350,
    "weapon_nova": 1050,
    "weapon_xm1014": 2000,
    "weapon_sawedoff": 1100,
    "weapon_mag7": 1300,
    "weapon_galilar": 1800,
    "weapon_ak47": 2700,
    "weapon_sg556": 3000,
    "weapon_famas": 1950,
    "weapon_m4a1": 2900,
    "weapon_m4a1_silencer": 2900,
    "weapon_aug": 3300,
    "weapon_ssg08": 1700,
    "weapon_awp": 4750,
    "weapon_scar20": 5000,
    "weapon_g3sg1": 5000,
    "weapon_negev": 1700,
    "weapon_m249": 5200,
    "weapon_flashbang": 200,
    "weapon_hegrenade": 300,
    "weapon_smokegrenade": 300,
    "weapon_molotov": 400,
    "weapon_incgrenade": 500,
    "weapon_decoy": 50,
}

WEAPON_DEF_INDEXES = {
    "weapon_deagle": 1,
    "weapon_elite": 2,
    "weapon_fiveseven": 3,
    "weapon_glock": 4,
    "weapon_ak47": 7,
    "weapon_aug": 8,
    "weapon_awp": 9,
    "weapon_famas": 10,
    "weapon_g3sg1": 11,
    "weapon_galilar": 13,
    "weapon_m249": 14,
    "weapon_m4a1": 16,
    "weapon_mac10": 17,
    "weapon_p90": 19,
    "weapon_mp5sd": 23,
    "weapon_ump45": 24,
    "weapon_xm1014": 25,
    "weapon_bizon": 26,
    "weapon_mag7": 27,
    "weapon_negev": 28,
    "weapon_sawedoff": 29,
    "weapon_tec9": 30,
    "weapon_taser": 31,
    "weapon_hkp2000": 32,
    "weapon_mp7": 33,
    "weapon_mp9": 34,
    "weapon_nova": 35,
    "weapon_p250": 36,
    "weapon_scar20": 38,
    "weapon_sg556": 39,
    "weapon_ssg08": 40,
    "weapon_knife": 42,
    "weapon_knife_t": 42,
    "weapon_flashbang": 43,
    "weapon_hegrenade": 44,
    "weapon_smokegrenade": 45,
    "weapon_molotov": 46,
    "weapon_decoy": 47,
    "weapon_incgrenade": 48,
    "weapon_c4": 49,
    "weapon_m4a1_silencer": 60,
    "weapon_usp_silencer": 61,
    "weapon_cz75a": 63,
    "weapon_revolver": 64,
}

def main() -> int:
    parser = argparse.ArgumentParser(description="Build pro CS2 opening replay datasets from HLTV demos.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    discover_parser = subparsers.add_parser("discover-hltv", help="Find match/demo links from an HLTV result page.")
    discover_parser.add_argument("--results-url", default=DEFAULT_RESULTS_URL)
    discover_parser.add_argument("--limit", type=int, default=10)
    discover_parser.add_argument("--proxy", help="HTTP(S) proxy URL. Defaults to proxyon environment variables.")

    download_parser = subparsers.add_parser("download-hltv", help="Download HLTV demo archives and extract .dem files.")
    download_parser.add_argument("--results-url", default=DEFAULT_RESULTS_URL)
    download_parser.add_argument("--match-url", action="append", default=[])
    download_parser.add_argument("--demo-url", action="append", default=[])
    download_parser.add_argument("--limit", type=int, default=3)
    download_parser.add_argument("--out", default="downloads/hltv_dust2")
    download_parser.add_argument("--proxy", help="HTTP(S) proxy URL. Defaults to proxyon environment variables.")
    download_parser.add_argument("--sleep", type=float, default=1.5)

    extract_parser = subparsers.add_parser("extract", help="Extract opening routes directly from .dem files to .cs2rec records and a runtime manifest.")
    extract_parser.add_argument("demos", nargs="+", help="Demo/archive files or directories containing .dem/.rar/.zip/.7z files.")
    extract_parser.add_argument("--export-manifest", default="data/{map}_openings_manifest.json", help="Manifest path or template. Use {map} for multi-map extraction.")
    extract_parser.add_argument("--records-dir", help="Directory/template for generated .cs2rec files. Defaults to <manifest stem>_records next to each manifest.")
    extract_parser.add_argument("--map", default="", help="Optional map filter. Empty means parse every demo and route by its actual map.")
    extract_parser.add_argument("--max-round-seconds", type=float, default=140.0, help="Fallback round length only when round_end/next freeze is unavailable.")
    extract_parser.add_argument("--economy-sample-seconds", type=float, default=2.0, help="Seconds after round_freeze_end used to sample pro balance/loadout/team economy.")
    extract_parser.add_argument("--tickrate", type=int, default=64)
    extract_parser.add_argument("--stride", type=int, default=1)
    extract_parser.add_argument("--demo-source", default="hltv")
    extract_parser.add_argument("--work-dir", default="data/archive_work", help="Temporary/cache directory for extracted archive members.")
    extract_parser.add_argument("--keep-extracted", action="store_true", help="Keep extracted .dem files in --work-dir for debugging/reuse.")
    extract_parser.add_argument("--scan-all-archive-demos", action="store_true", help="Extract all .dem files when archive names do not reveal the target map.")
    extract_parser.add_argument("--limit", type=int, help="Maximum number of .dem files to parse.")
    extract_parser.add_argument("--jobs", type=int, default=0, help="Parallel demo parser workers. 0 = auto.")
    extract_parser.add_argument("--max-tasks-per-child", type=int, default=1, help="Recycle parser workers after this many demo tasks. 1 releases per-demo parser memory most aggressively; 0 disables recycling.")
    extract_parser.add_argument("--stale-work-hours", type=float, default=12.0, help="Delete stale extractor run directories under --work-dir older than this many hours. 0 disables pruning.")
    extract_parser.add_argument("--progress", action="store_true", help="Show a tqdm progress bar while demo tasks complete.")
    extract_parser.add_argument("--reset", action="store_true", help="Delete the generated manifest and records directory before extracting.")
    extract_parser.add_argument("--strict", action="store_true", help="Abort on the first archive/demo parsing error instead of skipping bad files.")
    extract_parser.add_argument("--pretty-json", action="store_true", help="Pretty-print runtime manifest.")
    extract_parser.add_argument("--cs2rec-downsample", type=int, default=4, help="v4 origin sample stride. 1 disables origin downsampling.")
    extract_parser.add_argument("--no-cs2rec-downsample", action="store_true", help="Write every origin snapshot in v4 records.")

    args = parser.parse_args()
    if args.command == "discover-hltv":
        session = make_session(args.proxy)
        links = discover_hltv_demo_links(session, args.results_url, args.limit)
        print(json.dumps(links, indent=2, ensure_ascii=False))
        return 0
    if args.command == "download-hltv":
        session = make_session(args.proxy)
        download_hltv(args, session)
        return 0
    if args.command == "extract":
        export_direct_cs2rec(args)
        return 0
    raise AssertionError(args.command)


def make_session(proxy: str | None) -> requests.Session:
    session = requests.Session()
    session.headers.update({"User-Agent": DEFAULT_USER_AGENT, "Referer": "https://www.hltv.org/"})
    if proxy:
        session.proxies.update({"http": proxy, "https": proxy})
    return session


def discover_hltv_demo_links(session: requests.Session, results_url: str, limit: int) -> list[dict[str, str]]:
    html = fetch_text(session, results_url)
    match_paths = unique(re.findall(r'href="(/matches/\d+/[^"]+)"', html))
    if not match_paths and "cf-mitigated" in html.lower():
        raise RuntimeError("HLTV returned a Cloudflare challenge. Run proxyon or provide direct --match-url/--demo-url values.")

    links: list[dict[str, str]] = []
    for match_path in match_paths[:limit]:
        match_url = urljoin("https://www.hltv.org", match_path)
        match_html = fetch_text(session, match_url)
        demo_paths = unique(re.findall(r'"(/download/demo/[^"]+)"', match_html))
        for demo_path in demo_paths:
            links.append({"matchUrl": match_url, "demoUrl": urljoin("https://www.hltv.org", demo_path)})
            if len(links) >= limit:
                return links
        time.sleep(1.0)
    return links


def download_hltv(args: argparse.Namespace, session: requests.Session) -> None:
    output_dir = Path(args.out)
    archive_dir = output_dir / "archives"
    demo_dir = output_dir / "demos"
    archive_dir.mkdir(parents=True, exist_ok=True)
    demo_dir.mkdir(parents=True, exist_ok=True)

    demo_urls = list(args.demo_url)
    for match_url in args.match_url:
        match_html = fetch_text(session, match_url)
        demo_urls.extend(urljoin("https://www.hltv.org", path) for path in unique(re.findall(r'"(/download/demo/[^"]+)"', match_html)))

    if not demo_urls:
        links = discover_hltv_demo_links(session, args.results_url, args.limit)
        demo_urls.extend(link["demoUrl"] for link in links)

    if not demo_urls:
        raise RuntimeError("No demo URLs found.")

    for demo_url in unique(demo_urls)[: args.limit]:
        archive_path = download_demo_archive(session, demo_url, archive_dir)
        extract_archive(archive_path, demo_dir)
        time.sleep(args.sleep)

    demos = sorted(str(path) for path in demo_dir.rglob("*.dem"))
    print(json.dumps({"demoDir": str(demo_dir), "demos": demos}, indent=2))


def fetch_text(session: requests.Session, url: str) -> str:
    response = session.get(url, timeout=45)
    if response.status_code == 403:
        raise RuntimeError(
            f"HLTV returned 403 for {url}. The current proxy path is still blocked; "
            "solve the page in a browser and pass --match-url/--demo-url, or use a proxy/session that carries valid HLTV cookies."
        )
    response.raise_for_status()
    return response.text


def download_demo_archive(session: requests.Session, demo_url: str, archive_dir: Path) -> Path:
    response = session.get(demo_url, allow_redirects=True, stream=True, timeout=90)
    if response.status_code == 403:
        raise RuntimeError(
            f"HLTV returned 403 for {demo_url}. Use a browser-solved direct URL/cookie-bearing session or another proxy."
        )
    response.raise_for_status()

    final_name = response.url.rsplit("/", 1)[-1] or demo_url.rsplit("/", 1)[-1]
    if not re.search(r"\.(zip|rar|7z|dem)$", final_name, re.IGNORECASE):
        final_name = f"hltv_demo_{int(time.time())}.zip"
    archive_path = archive_dir / sanitize_filename(final_name)

    with archive_path.open("wb") as archive_file:
        for chunk in response.iter_content(chunk_size=1024 * 512):
            if chunk:
                archive_file.write(chunk)

    print(f"Downloaded {archive_path}")
    return archive_path


def extract_archive(archive_path: Path, output_dir: Path) -> None:
    if archive_path.suffix.lower() == ".dem":
        shutil.copy2(archive_path, output_dir / archive_path.name)
        return
    if archive_path.suffix.lower() == ".zip":
        subprocess.run(["unzip", "-o", str(archive_path), "-d", str(output_dir)], check=True)
        return
    if archive_path.suffix.lower() == ".rar" and shutil.which("unrar"):
        subprocess.run([shutil.which("unrar") or "unrar", "x", "-y", str(archive_path), str(output_dir) + "/"], check=True)
        return
    seven_zip = shutil.which("7z")
    if not seven_zip:
        raise RuntimeError(f"Need unrar or 7z to extract {archive_path}")
    subprocess.run([seven_zip, "x", "-y", f"-o{output_dir}", str(archive_path)], check=True)








def install_interrupt_signal_handlers() -> None:
    def raise_keyboard_interrupt(_signum: int, _frame: Any) -> None:
        raise KeyboardInterrupt

    signal.signal(signal.SIGTERM, raise_keyboard_interrupt)


def prepare_export_work_dir(args: argparse.Namespace) -> Path | None:
    work_root = Path(args.work_dir)
    work_root.mkdir(parents=True, exist_ok=True)
    if args.keep_extracted:
        return None

    prune_stale_work_dirs(work_root, float(args.stale_work_hours or 0.0))
    run_name = f"run_{dt.datetime.now(dt.UTC).strftime('%Y%m%d_%H%M%S')}_{os.getpid()}"
    run_dir = work_root / run_name
    run_dir.mkdir(parents=True, exist_ok=False)
    args.work_dir = str(run_dir)
    return run_dir


def prune_stale_work_dirs(work_root: Path, stale_hours: float) -> None:
    if stale_hours <= 0:
        return
    cutoff = time.time() - stale_hours * 3600.0
    for child in work_root.iterdir():
        if not child.is_dir() or not child.name.startswith("run_"):
            continue
        try:
            if child.stat().st_mtime < cutoff:
                shutil.rmtree(child, ignore_errors=True)
        except OSError:
            continue


def make_progress_bar(enabled: bool, total: int, description: str) -> Any | None:
    if not enabled:
        return None
    try:
        from tqdm import tqdm
    except ImportError:
        print("tqdm is not installed; continuing without a progress bar.")
        return None
    return tqdm(total=total, desc=description, unit="demo")


def update_progress_bar(progress_bar: Any | None, step: int = 1) -> None:
    if progress_bar is not None:
        progress_bar.update(step)


def close_progress_bar(progress_bar: Any | None) -> None:
    if progress_bar is not None:
        progress_bar.close()


def export_direct_cs2rec(args: argparse.Namespace) -> None:
    install_interrupt_signal_handlers()
    if getattr(args, "no_cs2rec_downsample", False):
        args.cs2rec_downsample = 1
    args.cs2rec_downsample = max(1, int(getattr(args, "cs2rec_downsample", 4) or 4))
    original_work_dir = args.work_dir
    run_work_dir = prepare_export_work_dir(args)
    progress_bar = None
    executor: concurrent.futures.ProcessPoolExecutor | None = None
    try:
        if args.reset:
            reset_export_outputs(args)

        sources = collect_source_paths(args.demos)
        if not sources:
            raise SystemExit("No .dem/.rar/.zip/.7z files found.")

        tasks = collect_demo_tasks(sources, args)
        if args.limit is not None:
            tasks = tasks[: max(0, args.limit)]
        if not tasks:
            raise SystemExit("No demo files found in sources.")

        worker_count = int(args.jobs or 0)
        if worker_count <= 0:
            worker_count = max(1, min(len(tasks), (os.cpu_count() or 1)))
        else:
            worker_count = max(1, min(worker_count, len(tasks)))

        max_tasks_per_child = int(args.max_tasks_per_child or 0)
        max_tasks_display = max_tasks_per_child if max_tasks_per_child > 0 else "disabled"
        print(f"Exporting {len(tasks)} demo task(s) with {worker_count} worker(s); max_tasks_per_child={max_tasks_display}.")
        options = vars(args).copy()
        rounds_by_map: dict[str, list[dict[str, Any]]] = {}
        parsed_by_map: dict[str, int] = {}
        failed = 0
        progress_bar = make_progress_bar(bool(args.progress), len(tasks), "extract demos")

        if worker_count == 1:
            for task in tasks:
                result = export_demo_task(task, options)
                failed += consume_export_result(result, rounds_by_map, parsed_by_map)
                update_progress_bar(progress_bar)
        else:
            executor_kwargs: dict[str, Any] = {"max_workers": worker_count}
            if max_tasks_per_child > 0:
                executor_kwargs["max_tasks_per_child"] = max_tasks_per_child
            executor = concurrent.futures.ProcessPoolExecutor(**executor_kwargs)
            future_by_task = {executor.submit(export_demo_task, task, options): task for task in tasks}
            try:
                for future in concurrent.futures.as_completed(future_by_task):
                    try:
                        result = future.result()
                    except Exception as error:
                        task = future_by_task[future]
                        failed += 1
                        print(f"Worker failed for {task.get('source_label', task.get('path'))}: {error}")
                        if args.strict:
                            raise
                    else:
                        failed += consume_export_result(result, rounds_by_map, parsed_by_map)
                    finally:
                        update_progress_bar(progress_bar)
            except KeyboardInterrupt:
                for future in future_by_task:
                    future.cancel()
                executor.shutdown(wait=False, cancel_futures=True)
                executor = None
                raise

        if executor is not None:
            executor.shutdown()
            executor = None

        for map_name in sorted(rounds_by_map):
            manifest_path = resolve_manifest_path(args, map_name)
            manifest_path.parent.mkdir(parents=True, exist_ok=True)
            rounds = sorted(rounds_by_map[map_name], key=lambda item: (str(item.get("demoPath", "")), int(item.get("roundNumber", 0))))
            manifest = {
                "format": "pro_opening_replay_cs2rec_v4",
                "mapName": map_name,
                "tickRate": args.tickrate,
                "cs2recVersion": 4,
                "transformDownsample": args.cs2rec_downsample,
                "rounds": rounds,
            }
            manifest_path.write_text(
                json.dumps(
                    manifest,
                    indent=2 if args.pretty_json else None,
                    ensure_ascii=False,
                    separators=None if args.pretty_json else (",", ":"),
                ),
                encoding="utf-8",
            )
            print(f"Wrote {len(rounds)} rounds from {parsed_by_map.get(map_name, 0)} demo file(s) to {manifest_path}")

        total_rounds = sum(len(rounds) for rounds in rounds_by_map.values())
        print(f"Done: {total_rounds} rounds across {len(rounds_by_map)} map(s); failed/skipped tasks={failed}.")
    finally:
        if executor is not None:
            executor.shutdown(wait=False, cancel_futures=True)
        close_progress_bar(progress_bar)
        if run_work_dir is not None and not args.keep_extracted:
            shutil.rmtree(run_work_dir, ignore_errors=True)
        args.work_dir = original_work_dir


def export_direct_demo(
    demo_path: Path,
    args: argparse.Namespace,
    source_label: str,
) -> tuple[str, list[dict[str, Any]]]:
    parser = DemoParser(str(demo_path))
    header = parser.parse_header()
    map_name = header.get("map_name") or header.get("mapName") or ""
    if not map_name:
        print(f"Skipping {demo_path}: no map name in demo header")
        return "", []
    if args.map and not map_matches(args.map, map_name):
        print(f"Skipping {demo_path}: map {map_name!r} != {args.map!r}")
        return "", []
    stored_map_name = canonical_map_name(args.map, map_name)
    manifest_path = resolve_manifest_path(args, stored_map_name)
    manifest_dir = manifest_path.parent
    record_root = resolve_records_dir(args, manifest_path, stored_map_name)
    record_root.mkdir(parents=True, exist_ok=True)
    bundle_key = demo_output_key(source_label)
    bundle_path = record_root / f"{bundle_key}.cs2rec"
    bundle_rec_path = relative_manifest_path(bundle_path, manifest_dir)
    bundle_entries: list[Cs2RecBundleEntry] = []

    freeze_events = parser.parse_event("round_freeze_end")
    if freeze_events.empty:
        print(f"Skipping {demo_path}: no round_freeze_end events")
        return stored_map_name, []

    try:
        plant_events = parser.parse_event("bomb_planted")
    except Exception as plant_error:
        print(f"Plant event parse failed: {plant_error}")
        plant_events = None
    try:
        round_end_events = parser.parse_event("round_end")
    except Exception as round_end_error:
        print(f"Round-end event parse failed: {round_end_error}")
        round_end_events = None

    freeze_ticks = sorted(int(tick) for tick in freeze_events["tick"].dropna().unique())
    max_round_ticks = int(args.max_round_seconds * args.tickrate)
    stride = max(1, int(args.stride))
    wanted_tick_set: set[int] = set()
    economy_sample_ticks: dict[int, int] = {}
    for index, freeze_tick in enumerate(freeze_ticks):
        next_freeze_tick = freeze_ticks[index + 1] if index + 1 < len(freeze_ticks) else None
        round_end_tick = find_round_end_tick_for_round(round_end_events, freeze_tick, next_freeze_tick)
        if round_end_tick is None:
            round_end_tick = (next_freeze_tick - 1) if next_freeze_tick is not None else freeze_tick + max_round_ticks
        economy_sample_tick = min(round_end_tick, freeze_tick + max(0, int(round(args.economy_sample_seconds * args.tickrate))))
        economy_sample_ticks[freeze_tick] = economy_sample_tick
        wanted_tick_set.add(economy_sample_tick)
        wanted_tick_set.update(range(freeze_tick, round_end_tick + 1, stride))
    wanted_ticks = sorted(wanted_tick_set)
    tick_data = parse_ticks_strict(parser, wanted_ticks)
    grenade_data = parse_grenades(parser)
    if tick_data.empty or "tick" not in tick_data:
        print(f"Skipping {demo_path}: no tick rows")
        return stored_map_name, []

    rounds: list[dict[str, Any]] = []
    for index, freeze_tick in enumerate(freeze_ticks):
        next_freeze_tick = freeze_ticks[index + 1] if index + 1 < len(freeze_ticks) else None
        freeze_rows = tick_data[tick_data["tick"] == freeze_tick] if "tick" in tick_data else tick_data.iloc[0:0]
        if freeze_rows.empty:
            continue
        round_number = direct_round_number(freeze_rows, freeze_tick)
        if round_number is None:
            continue

        payload = build_direct_round_manifest(
            source_label=source_label,
            map_name=stored_map_name,
            round_number=round_number,
            freeze_tick=freeze_tick,
            round_end_tick=find_round_end_tick_for_round(round_end_events, freeze_tick, next_freeze_tick),
            max_round_ticks=max_round_ticks,
            next_freeze_tick=next_freeze_tick,
            economy_sample_tick=economy_sample_ticks.get(freeze_tick, freeze_tick),
            tickrate=args.tickrate,
            tick_data=tick_data,
            freeze_rows=freeze_rows,
            plant_events=plant_events,
            grenade_data=grenade_data,
            manifest_dir=manifest_dir,
            record_root=record_root,
            bundle_rec_path=bundle_rec_path,
            bundle_entries=bundle_entries,
            transform_downsample=args.cs2rec_downsample,
        )
        if payload["players"]:
            rounds.append(payload)

    if bundle_entries:
        write_cs2rec_bundle(bundle_path, bundle_entries)

    return stored_map_name, rounds


def direct_round_number(freeze_rows: Any, freeze_tick: int) -> int | None:
    if "total_rounds_played" not in freeze_rows:
        return None
    values = freeze_rows["total_rounds_played"].dropna()
    if values.empty:
        return None
    return int(values.iloc[0])


def build_direct_round_manifest(
    *,
    source_label: str,
    map_name: str,
    round_number: int,
    freeze_tick: int,
    round_end_tick: int | None,
    max_round_ticks: int,
    next_freeze_tick: int | None,
    economy_sample_tick: int,
    tickrate: int,
    tick_data: Any,
    freeze_rows: Any,
    plant_events: Any,
    grenade_data: Any,
    manifest_dir: Path,
    record_root: Path,
    bundle_rec_path: str,
    bundle_entries: list[Cs2RecBundleEntry],
    transform_downsample: int,
) -> dict[str, Any]:
    if round_end_tick is None:
        round_end_tick = (next_freeze_tick - 1) if next_freeze_tick is not None else freeze_tick + max_round_ticks
    plant_tick, plant_pos = find_plant_for_round(plant_events, tick_data, freeze_tick, next_freeze_tick)
    if "total_rounds_played" in tick_data:
        round_rows = tick_data[
            (tick_data["total_rounds_played"] == round_number)
            & (tick_data["tick"] >= freeze_tick)
            & (tick_data["tick"] <= round_end_tick)
        ].copy()
    else:
        round_rows = tick_data[(tick_data["tick"] >= freeze_tick) & (tick_data["tick"] <= round_end_tick)].copy()

    round_rows = filter_replay_rows(round_rows)
    active_rows = active_replay_rows(round_rows)
    if not active_rows.empty:
        round_rows = active_rows
    economy_rows = sample_round_rows_at_tick(round_rows, economy_sample_tick)
    if economy_rows.empty:
        economy_rows = freeze_rows

    round_key = round_output_key(source_label, round_number)
    players = []
    slot_by_steamid = direct_slot_map(freeze_rows, round_rows)
    steam_col = steamid_column(round_rows) if not round_rows.empty else steamid_column(freeze_rows)
    for steamid_value, player_rows in round_rows.groupby(steam_col):
        steamid = steamid_text(steamid_value)
        if not steamid:
            continue

        player_rows = player_rows.sort_values("tick").drop_duplicates(subset=["tick"], keep="first")
        if len(player_rows) < 2:
            continue
        freeze_steam_col = steamid_column(freeze_rows)
        freeze_player_rows = freeze_rows[freeze_rows[freeze_steam_col].map(steamid_text) == steamid]
        baseline = sample_player_row(player_rows, economy_sample_tick, freeze_player_rows)
        team_num = int(row_value(baseline, "team_num", 0) or 0)
        if team_num not in (2, 3):
            continue

        frames = [
            direct_frame_from_tick_row(row.to_dict(), freeze_tick, tickrate)
            for _, row in player_rows.iterrows()
        ]
        frames = [frame for frame in frames if int(frame["relative_tick"]) >= 0]
        frames = infer_frame_sequence(frames, tickrate)
        if len(frames) < 2:
            continue
        subticks_by_tick = {
            int(row["tick"]): build_replay_subticks(row.to_dict())
            for _, row in player_rows.iterrows()
        }
        subticks_by_tick = {tick: moves for tick, moves in subticks_by_tick.items() if moves}

        inventory = normalize_inventory(row_value(baseline, "inventory", []))
        inventory_def_indexes = normalize_inventory_def_indexes(row_value(baseline, "inventory_as_ids", []))
        player_name = str(row_value(baseline, "player_name", row_value(baseline, "name", steamid)) or steamid)
        safe_player = sanitize_filename(f"{team_num}_{slot_by_steamid.get(steamid, 0)}_{steamid or player_name}")
        rec_key = f"{round_key}/{safe_player}_round"
        route_entry = build_cs2rec_route_entry(
            rec_key,
            frames,
            map_name,
            round_number,
            team_num,
            steamid,
            player_name,
            tickrate,
            subticks_by_tick=subticks_by_tick,
            transform_downsample=transform_downsample,
        )
        if route_entry is None:
            continue
        bundle_entries.append(route_entry)
        round_info = cs2rec_segment_info(frames, list(route_entry.weapon_defs))
        if round_info is None:
            continue

        retake_info = None
        retake_start = None
        if plant_tick is not None:
            retake_start = first_frame_index_at_or_after(frames, int(plant_tick))
            if retake_start is not None and len(frames) - retake_start >= 2:
                retake_info = cs2rec_segment_info(frames[retake_start:])

        player_payload = {
            "steamId": steamid,
            "name": player_name,
            "teamNum": team_num,
            "slot": int(slot_by_steamid.get(steamid, 0)),
            "startBalance": int(row_value(baseline, "balance", 0) or 0),
            "balance": int(row_value(baseline, "balance", 0) or 0),
            "economySampleRelativeTick": max(0, economy_sample_tick - freeze_tick),
            "economySampleTime": rounded_float(max(0, economy_sample_tick - freeze_tick) / tickrate, 4),
            "equipmentValue": int(row_value(baseline, "current_equip_value", row_value(baseline, "round_start_equip_value", 0)) or 0),
            "armorValue": int(row_value(baseline, "armor_value", 0) or 0),
            "hasHelmet": bool(row_value(baseline, "has_helmet", False)),
            "hasDefuser": bool(row_value(baseline, "has_defuser", False)),
            "inventory": inventory,
            "inventoryDefIndexes": inventory_def_indexes,
            "recPath": bundle_rec_path,
            "recKey": rec_key,
            "duration": rounded_float(round_info["duration"], 4),
            "firstWeaponDefIndex": round_info["firstWeaponDefIndex"],
            "preloadWeaponDefIndexes": round_info["preloadWeaponDefIndexes"],
            "startFrame": round_info["startFrame"],
            "endFrame": round_info["endFrame"],
            "grenades": direct_grenades_for_player(grenade_data, round_rows, steamid, freeze_tick, round_end_tick - freeze_tick, tickrate),
        }
        if retake_info is not None:
            player_payload.update(
                {
                    "retakeStartTickIndex": max(0, int(retake_start or 0)),
                    "retakeStartRelativeTick": max(0, int(plant_tick) - freeze_tick),
                    "retakeStartTime": rounded_float((int(plant_tick) - freeze_tick) / tickrate, 4),
                    "retakeDuration": rounded_float(retake_info["duration"], 4),
                    "retakeStartFrame": retake_info["startFrame"],
                    "retakeEndFrame": retake_info["endFrame"],
                }
            )
        players.append(player_payload)

    payload = {
        "id": round_key,
        "demoPath": source_label,
        "roundNumber": round_number,
        "freezeEndTick": freeze_tick,
        "economySampleRelativeTick": max(0, economy_sample_tick - freeze_tick),
        "economySampleTime": rounded_float(max(0, economy_sample_tick - freeze_tick) / tickrate, 4),
        "teamEconomies": direct_team_economies(economy_rows),
        "players": sorted(players, key=lambda player: (int(player["teamNum"]), int(player["slot"]))),
    }
    if plant_tick is not None:
        payload["plantRelativeTick"] = int(plant_tick) - freeze_tick
        if plant_pos is not None:
            payload["plantPos"] = {
                "x": rounded_float(plant_pos[0]),
                "y": rounded_float(plant_pos[1]),
                "z": rounded_float(plant_pos[2]),
            }
    return payload


def filter_replay_rows(rows: Any) -> Any:
    if rows.empty:
        return rows
    if "team_num" in rows:
        rows = rows[rows["team_num"].isin([2, 3])]
    if "is_alive" in rows:
        rows = rows[rows["is_alive"] == True]
    steam_col = steamid_column(rows) if ("player_steamid" in rows or "steamid" in rows) else None
    if steam_col:
        rows = rows[rows[steam_col].map(lambda value: bool(steamid_text(value)))]
    return rows.copy()


def sample_round_rows_at_tick(rows: Any, sample_tick: int) -> Any:
    if rows.empty or "tick" not in rows:
        return rows.iloc[0:0].copy() if hasattr(rows, "iloc") else rows

    import pandas as pd

    sampled_rows = []
    steam_col = steamid_column(rows)
    for _, player_rows in rows.groupby(steam_col):
        sampled_rows.append(sample_player_row(player_rows.sort_values("tick"), sample_tick).to_dict())
    return pd.DataFrame(sampled_rows) if sampled_rows else rows.iloc[0:0].copy()


def sample_player_row(player_rows: Any, sample_tick: int, fallback_rows: Any | None = None) -> Any:
    if player_rows is not None and not player_rows.empty and "tick" in player_rows:
        after = player_rows[player_rows["tick"] >= sample_tick]
        if not after.empty:
            return after.iloc[0]

        before = player_rows[player_rows["tick"] <= sample_tick]
        if not before.empty:
            return before.iloc[-1]

        return player_rows.iloc[0]

    if fallback_rows is not None and not fallback_rows.empty:
        return fallback_rows.iloc[0]

    return player_rows.iloc[0]


def active_replay_rows(rows: Any) -> Any:
    if rows.empty:
        return rows
    mask = None
    if "round_in_progress" in rows:
        mask = rows["round_in_progress"].fillna(False).astype(bool)
    if "is_freeze_period" in rows:
        not_freeze = ~rows["is_freeze_period"].fillna(False).astype(bool)
        mask = not_freeze if mask is None else (mask & not_freeze)
    return rows[mask].copy() if mask is not None else rows.iloc[0:0]


def direct_slot_map(freeze_rows: Any, round_rows: Any) -> dict[str, int]:
    slot_by_steamid: dict[str, int] = {}
    candidates = freeze_rows
    if candidates.empty:
        candidates = round_rows
    if candidates.empty:
        return slot_by_steamid
    try:
        steam_col = steamid_column(candidates)
    except KeyError:
        return slot_by_steamid
    ordered = []
    if "team_num" in candidates:
        candidates = candidates[candidates["team_num"].isin([2, 3])]
    for _, row in candidates.sort_values(["team_num", steam_col] if "team_num" in candidates else [steam_col]).iterrows():
        steamid = steamid_text(row.get(steam_col))
        if steamid and steamid not in ordered:
            ordered.append(steamid)
    for slot_index, steamid in enumerate(ordered):
        slot_by_steamid[steamid] = slot_index
    return slot_by_steamid


def direct_frame_from_tick_row(row: dict[str, Any], freeze_tick: int, tickrate: int) -> dict[str, Any]:
    tick = required_int(row, "tick")
    eye_angles = optional_vector(row, "CCSPlayerPawn.m_angEyeAngles", 3)
    pitch = eye_angles[0] if eye_angles is not None else first_finite_float(
        row,
        "pitch",
        "usercmd_viewangle_x",
        default=0.0,
    )
    yaw = eye_angles[1] if eye_angles is not None else first_finite_float(
        row,
        "yaw",
        "usercmd_viewangle_y",
        default=0.0,
    )
    roll = eye_angles[2] if eye_angles is not None else 0.0
    ladder_normal = optional_vector(
        row,
        "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal",
        3,
    ) or [0.0, 0.0, 0.0]
    active_weapon = normalize_item(row_value(row, "active_weapon_name", ""))
    explicit_weapon_def = optional_int(row, "item_def_idx")
    weapon_def = normalize_weapon_def_index(explicit_weapon_def) if explicit_weapon_def is not None else weapon_def_index(active_weapon)
    buttons = first_int(row, "usercmd_buttonstate_1", "buttons", default=0)
    buttons1 = first_int(row, "usercmd_buttonstate_2", default=0)
    buttons2 = first_int(row, "usercmd_buttonstate_3", default=0)
    entity_flags = first_int(row, "CCSPlayerPawn.m_fFlags", default=inferred_entity_flags(row))
    move_type = first_int(row, "CCSPlayerPawn.m_MoveType", "move_type", default=2)
    actual_move_type = first_int(row, "CCSPlayerPawn.m_nActualMoveType", default=move_type)
    inferred_duck = 1 if (entity_flags & (1 << 1)) or (buttons & (1 << 2)) else 0
    ducked = first_int(row, "ducked", default=inferred_duck)
    ducking = first_int(row, "ducking", default=inferred_duck)
    desires_duck = first_int(
        row,
        "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
        default=inferred_duck,
    )
    return {
        "tick": tick,
        "relative_tick": tick - freeze_tick,
        "time_seconds": (tick - freeze_tick) / tickrate,
        "x": required_float(row, "X"),
        "y": required_float(row, "Y"),
        "z": required_float(row, "Z"),
        "velocity_x": first_finite_float(row, "velocity_X", default=math.nan),
        "velocity_y": first_finite_float(row, "velocity_Y", default=math.nan),
        "velocity_z": first_finite_float(row, "velocity_Z", default=math.nan),
        "pitch": float(pitch),
        "yaw": float(yaw),
        "roll": float(roll),
        "entity_flags": entity_flags,
        "move_type": move_type,
        "actual_move_type": actual_move_type,
        "buttons": buttons,
        "buttons1": buttons1,
        "buttons2": buttons2,
        "duck_amount": first_finite_float(row, "duck_amount", default=1.0 if ducking else 0.0),
        "duck_speed": first_finite_float(row, "duck_speed", default=8.0 if ducking else 0.0),
        "ladder_normal_x": float(ladder_normal[0]),
        "ladder_normal_y": float(ladder_normal[1]),
        "ladder_normal_z": float(ladder_normal[2]),
        "ducked": ducked,
        "ducking": ducking,
        "desires_duck": desires_duck,
        "active_weapon": active_weapon,
        "active_weapon_def_index": weapon_def,
    }


MAX_REPLAY_VELOCITY = 16384.0
MAX_PARSED_VELOCITY_DISAGREEMENT = 2048.0


def infer_frame_sequence(frames: list[dict[str, Any]], tickrate: int) -> list[dict[str, Any]]:
    for index, frame in enumerate(frames):
        for axis, key in enumerate(("velocity_x", "velocity_y", "velocity_z")):
            velocity = finite_float(frame.get(key))
            inferred_velocity = inferred_axis_velocity(frames, index, axis, tickrate)
            if (
                velocity is None
                or abs(velocity) > MAX_REPLAY_VELOCITY
                or (index < 2 and abs(velocity - inferred_velocity) > MAX_PARSED_VELOCITY_DISAGREEMENT)
            ):
                velocity = inferred_velocity
            frame[key] = clamp_float(velocity, -MAX_REPLAY_VELOCITY, MAX_REPLAY_VELOCITY)
    return frames


def inferred_axis_velocity(frames: list[dict[str, Any]], index: int, axis: int, tickrate: int) -> float:
    coord_key = ("x", "y", "z")[axis]
    current = frames[index]
    for other_index in (index + 1, index - 1):
        if other_index < 0 or other_index >= len(frames):
            continue
        other = frames[other_index]
        delta_ticks = int(other["tick"]) - int(current["tick"])
        if delta_ticks == 0:
            continue
        delta_pos = float(other[coord_key]) - float(current[coord_key])
        return delta_pos * float(tickrate) / float(delta_ticks)
    return 0.0


def clamp_float(value: float, lower: float, upper: float) -> float:
    if not math.isfinite(value):
        return 0.0
    return max(lower, min(upper, float(value)))


def direct_team_economies(freeze_rows: Any) -> list[dict[str, Any]]:
    if freeze_rows.empty or "team_num" not in freeze_rows:
        return []
    payloads: list[dict[str, Any]] = []
    for team_num, team_rows in freeze_rows.groupby("team_num"):
        if int(team_num) not in (2, 3):
            continue
        player_count = len(team_rows)
        total_start_balance = int(safe_sum(team_rows, "balance"))
        total_equipment_value = int(safe_sum(team_rows, "current_equip_value"))
        equipment_breakdown = summarize_equipment(team_rows)
        total_cash_equipment_value = total_start_balance + max(total_equipment_value, equipment_breakdown["totalValue"])
        payloads.append(
            {
                "teamNum": int(team_num),
                "teamName": first_text(team_rows, "team_clan_name", fallback=team_label(int(team_num))),
                "playerCount": player_count,
                "totalStartBalance": total_start_balance,
                "averageStartBalance": int(total_start_balance / max(1, player_count)),
                "totalEquipmentValue": total_equipment_value,
                "totalPrimaryValue": equipment_breakdown["primaryValue"],
                "totalUtilityValue": equipment_breakdown["utilityValue"],
                "totalArmorValue": equipment_breakdown["armorValue"],
                "totalCashEquipmentValue": total_cash_equipment_value,
            }
        )
    return payloads


def direct_grenades_for_player(
    grenade_data: Any,
    round_rows: Any,
    steamid: str,
    freeze_tick: int,
    opening_ticks: int,
    tickrate: int,
) -> list[dict[str, Any]]:
    if grenade_data.empty or "tick" not in grenade_data:
        return []
    entity_column = first_existing_column(grenade_data, "grenade_entity_id", "entity_id")
    steamid_column_name = first_existing_column(grenade_data, "steamid", "thrower_steamid", "thrower_steamID")
    type_column = first_existing_column(grenade_data, "grenade_type")
    x_column = first_existing_column(grenade_data, "x", "X")
    y_column = first_existing_column(grenade_data, "y", "Y")
    z_column = first_existing_column(grenade_data, "z", "Z")
    if not all([entity_column, steamid_column_name, type_column, x_column, y_column, z_column]):
        return []

    round_end_tick = freeze_tick + opening_ticks
    round_grenades = grenade_data[(grenade_data["tick"] >= freeze_tick) & (grenade_data["tick"] <= round_end_tick)].copy()
    if round_grenades.empty:
        return []

    payloads: list[dict[str, Any]] = []
    for _, trajectory in round_grenades.groupby(entity_column):
        trajectory = trajectory.sort_values("tick")
        positioned_trajectory = trajectory.dropna(subset=[x_column, y_column, z_column])
        if positioned_trajectory.empty:
            continue
        first = positioned_trajectory.iloc[0]
        if steamid_text(first.get(steamid_column_name)) != steamid:
            continue
        second = positioned_trajectory.iloc[1] if len(positioned_trajectory) > 1 else first
        tick = int(first.get("tick", freeze_tick))
        if tick < freeze_tick:
            continue

        player_frame = nearest_player_frame(round_rows, steamid, tick)
        delta_ticks = max(1, int(second.get("tick", tick)) - tick)
        velocity_scale = tickrate / delta_ticks
        velocity_x = (float(second.get(x_column, first.get(x_column, 0.0))) - float(first.get(x_column, 0.0))) * velocity_scale
        velocity_y = (float(second.get(y_column, first.get(y_column, 0.0))) - float(first.get(y_column, 0.0))) * velocity_scale
        velocity_z = (float(second.get(z_column, first.get(z_column, 0.0))) - float(first.get(z_column, 0.0))) * velocity_scale
        payloads.append(
            {
                "relativeTick": tick - freeze_tick,
                "time": rounded_float((tick - freeze_tick) / tickrate, 4),
                "type": normalize_grenade_type(first.get(type_column, "")),
                "x": rounded_float(first.get(x_column, 0.0)),
                "y": rounded_float(first.get(y_column, 0.0)),
                "z": rounded_float(first.get(z_column, 0.0)),
                "pitch": rounded_float(player_frame.get("pitch", 0.0) if player_frame is not None else 0.0),
                "yaw": rounded_float(player_frame.get("yaw", 0.0) if player_frame is not None else 0.0),
                "velocityX": rounded_float(velocity_x),
                "velocityY": rounded_float(velocity_y),
                "velocityZ": rounded_float(velocity_z),
            }
        )
    payloads.sort(key=lambda grenade: grenade["time"])
    return payloads


def parse_ticks_strict(parser: DemoParser, wanted_ticks: list[int]):
    wanted_props = unique(ESSENTIAL_TICK_PROPS + OPTIONAL_TICK_PROPS + RECORDER_TICK_PROPS)
    try:
        return parser.parse_ticks(wanted_props, ticks=wanted_ticks)
    except Exception:
        if "usercmd_subtick_moves" not in wanted_props:
            raise
        return parser.parse_ticks(
            [prop for prop in wanted_props if prop != "usercmd_subtick_moves"],
            ticks=wanted_ticks,
        )


def parse_grenades(parser: DemoParser):
    import pandas as pd

    try:
        return parser.parse_grenades(extra=["total_rounds_played"])
    except Exception as error:
        print(f"Grenade parse failed: {error}")
        return pd.DataFrame()


def find_plant_tick_for_round(plant_events, freeze_tick: int, next_freeze_tick: int | None = None) -> int | None:
    if plant_events is None or getattr(plant_events, "empty", True):
        return None
    if "tick" not in plant_events:
        return None
    matches = plant_events[plant_events["tick"] >= freeze_tick]
    if next_freeze_tick is not None:
        matches = matches[matches["tick"] < next_freeze_tick]
    if matches.empty:
        return None
    return int(matches["tick"].iloc[0])


def find_round_end_tick_for_round(round_end_events, freeze_tick: int, next_freeze_tick: int | None = None) -> int | None:
    if round_end_events is None or getattr(round_end_events, "empty", True):
        return None
    if "tick" not in round_end_events:
        return None
    matches = round_end_events[round_end_events["tick"] >= freeze_tick]
    if next_freeze_tick is not None:
        matches = matches[matches["tick"] < next_freeze_tick]
    if matches.empty:
        return None
    return int(matches["tick"].iloc[0])


def find_plant_for_round(plant_events, tick_data, freeze_tick: int, next_freeze_tick: int | None = None):
    """Locate the bomb_planted tick that belongs to this round, and the C4's XYZ at that tick.

    Returns (plant_tick, (x, y, z)) or (None, None) if no plant happened in the captured window.
    Plant pos comes from any tick row at plant_tick (we look up by tick alone -- bomb pos is one of
    the per-player props but at plant time the planter's X/Y/Z is essentially the bomb pos)."""
    plant_tick = find_plant_tick_for_round(plant_events, freeze_tick, next_freeze_tick)
    if plant_tick is None:
        return None, None
    matches = plant_events[plant_events["tick"] == plant_tick]

    # Bomb position: at plant tick, any tick row's planter XYZ approximates the C4 location.
    # Use the planter's tick row if present, else fall back to first row at that tick.
    planter_steamid = None
    if "user_steamid" in matches.columns:
        planter_steamid = matches["user_steamid"].iloc[0]
    elif "userid_steamid" in matches.columns:
        planter_steamid = matches["userid_steamid"].iloc[0]
    plant_pos = None
    if "tick" in tick_data and not tick_data.empty:
        tick_rows = tick_data[tick_data["tick"] == plant_tick]
        if not tick_rows.empty:
            steam_col = steamid_column(tick_rows)
            if planter_steamid is not None and steam_col in tick_rows:
                planter_rows = tick_rows[tick_rows[steam_col].map(steamid_text) == steamid_text(planter_steamid)]
                if not planter_rows.empty:
                    row = planter_rows.iloc[0]
                    plant_pos = (float(row.get("X", 0) or 0), float(row.get("Y", 0) or 0), float(row.get("Z", 0) or 0))
            if plant_pos is None:
                playable_rows = tick_rows
                if "team_num" in playable_rows:
                    playable_rows = playable_rows[playable_rows["team_num"].isin([2, 3])]
                if steam_col in playable_rows:
                    playable_rows = playable_rows[playable_rows[steam_col].map(lambda value: bool(steamid_text(value)))]
                if not playable_rows.empty:
                    nonzero_rows = playable_rows[
                        (playable_rows["X"].fillna(0).astype(float) != 0)
                        | (playable_rows["Y"].fillna(0).astype(float) != 0)
                        | (playable_rows["Z"].fillna(0).astype(float) != 0)
                    ]
                    row = (nonzero_rows if not nonzero_rows.empty else playable_rows).iloc[0]
                else:
                    row = tick_rows.iloc[0]
                plant_pos = (float(row.get("X", 0) or 0), float(row.get("Y", 0) or 0), float(row.get("Z", 0) or 0))
    return plant_tick, plant_pos
























def write_cs2rec_rows(
    path: Path,
    rows: list[Any],
    map_name: str,
    round_number: int,
    team_num: int,
    steamid: str,
    player_name: str,
    tickrate: int,
    *,
    subticks_by_tick: dict[int, list[Any]] | None = None,
    transform_downsample: int = 4,
) -> dict[str, Any] | None:
    entry = build_cs2rec_route_entry(
        "route",
        rows,
        map_name,
        round_number,
        team_num,
        steamid,
        player_name,
        tickrate,
        subticks_by_tick=subticks_by_tick,
        transform_downsample=transform_downsample,
    )
    if entry is None:
        return None
    write_cs2rec_bundle(path, [entry])
    return cs2rec_segment_info(rows, list(entry.weapon_defs))


def build_cs2rec_route_entry(
    key: str,
    rows: list[Any],
    map_name: str,
    round_number: int,
    team_num: int,
    steamid: str,
    player_name: str,
    tickrate: int,
    *,
    subticks_by_tick: dict[int, list[Any]] | None = None,
    transform_downsample: int = 4,
) -> Cs2RecBundleEntry | None:
    if len(rows) < 2:
        return None
    subticks_by_tick = subticks_by_tick or {}
    transform_downsample = max(1, int(transform_downsample or 1))

    snapshots: list[bytes] = []
    weapon_defs: list[int] = []
    subtick_counts: list[int] = []
    subtick_records: list[bytes] = []
    snapshots.append(snapshot_bytes(rows, 0))
    for i in range(len(rows) - 1):
        tick = int(rows[i]["tick"])
        tick_subs = [subtick_bytes(row) for row in subticks_by_tick.get(tick, [])]
        subtick_records.extend(tick_subs)
        weapon_def = row_weapon_def(rows[i])
        if weapon_def < 0:
            weapon_def = row_weapon_def(rows[i + 1])
        weapon_defs.append(weapon_def)
        subtick_counts.append(len(tick_subs))
        snapshots.append(snapshot_bytes(rows, i + 1))

    if not weapon_defs:
        return None

    payload = bytearray()
    payload += struct.pack("<f", float(tickrate))
    payload += struct.pack("<I", max(0, round_number))
    payload += struct.pack("<B", max(0, min(255, team_num)))
    payload += struct.pack("<I", 0)
    payload += struct.pack("<Q", steamid_u64(steamid))
    write_varuint(payload, len(weapon_defs))
    write_varuint(payload, len(subtick_records))
    write_varuint(payload, len(snapshots))
    write_varuint(payload, transform_downsample)
    transform_sample_indexes = sampled_transform_indexes(len(snapshots), transform_downsample)
    write_varuint(payload, len(transform_sample_indexes))
    write_rec_string(payload, map_name)
    write_rec_string(payload, player_name)

    for index in transform_sample_indexes:
        write_varuint(payload, index)
        payload += snapshot_origin_bytes(snapshots[index])

    for snapshot in snapshots:
        payload += snapshot_angle_bytes(snapshot)

    move_types = [snapshot[40] for snapshot in snapshots]
    for snapshot in snapshots:
        payload += snapshot_velocity_bytes(snapshot)

    write_rle_varints(payload, [struct.unpack_from("<I", snapshot, 36)[0] for snapshot in snapshots], signed=False)
    write_rle_varints(payload, move_types, signed=False)
    write_u64_rle(payload, [struct.unpack_from("<Q", snapshot, 44)[0] for snapshot in snapshots])
    write_sparse_u64_rle(payload, [struct.unpack_from("<Q", snapshot, 52)[0] for snapshot in snapshots])
    write_sparse_u64_rle(payload, [struct.unpack_from("<Q", snapshot, 60)[0] for snapshot in snapshots])
    write_float_rle(payload, [snapshot[68:72] for snapshot in snapshots])
    write_float_rle(payload, [snapshot[72:76] for snapshot in snapshots])
    write_rle_varints(payload, [snapshot[88] for snapshot in snapshots], signed=False)
    write_rle_varints(payload, [snapshot[89] for snapshot in snapshots], signed=False)
    write_rle_varints(payload, [snapshot[90] for snapshot in snapshots], signed=False)
    write_sparse_varuint_override_rle(payload, [snapshot[91] for snapshot in snapshots], move_types)
    write_sparse_vec3_rle(payload, [
        snapshot[76:88] if move_type == MOVETYPE_LADDER else b"\x00" * 12
        for snapshot, move_type in zip(snapshots, move_types)
    ])
    write_rle_varints(payload, weapon_defs, signed=True)
    write_rle_varints(payload, subtick_counts, signed=False)
    for sub in subtick_records:
        payload += compact_subtick_bytes(sub)

    return Cs2RecBundleEntry(key=key, payload=bytes(payload), weapon_defs=tuple(weapon_defs))


def write_cs2rec_bundle(path: Path, entries: list[Cs2RecBundleEntry]) -> None:
    if not entries:
        return
    bundle = bytearray()
    write_varuint(bundle, len(entries))
    for entry in entries:
        write_rec_string(bundle, entry.key)
        write_varuint(bundle, len(entry.payload))
        bundle += entry.payload

    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as output:
        output.write(b"CS2BMREC")
        output.write(struct.pack("<I", 4))
        output.write(struct.pack("<B", 2))
        output.write(brotli.compress(bytes(bundle), quality=6))


def cs2rec_segment_info(rows: list[Any], weapon_defs: list[int] | None = None) -> dict[str, Any] | None:
    if len(rows) < 2:
        return None
    if weapon_defs is None:
        weapon_defs = []
        for i in range(len(rows) - 1):
            weapon_def = row_weapon_def(rows[i])
            if weapon_def < 0:
                weapon_def = row_weapon_def(rows[i + 1])
            weapon_defs.append(weapon_def)

    duration = (float(rows[-1]["time_seconds"]) - float(rows[0]["time_seconds"])) if len(rows) > 1 else 0.0
    preload_defs = sorted({normalize_weapon_def_index(defn) for defn in weapon_defs if is_preload_weapon_def(defn)})
    first_def = next((normalize_weapon_def_index(defn) for defn in weapon_defs if normalize_weapon_def_index(defn) >= 0), -1)
    return {
        "duration": max(0.0, duration),
        "firstWeaponDefIndex": first_def,
        "preloadWeaponDefIndexes": preload_defs,
        "startFrame": manifest_frame(rows[0], time_seconds=0.0),
        "endFrame": manifest_frame(rows[-1], time_seconds=max(0.0, duration)),
    }


def snapshot_bytes(rows: list[Any], index: int) -> bytes:
    row = rows[index]

    data = bytearray()
    data += struct.pack(
        "<fffffffff",
        required_float(row, "x"),
        required_float(row, "y"),
        required_float(row, "z"),
        required_float(row, "velocity_x"),
        required_float(row, "velocity_y"),
        required_float(row, "velocity_z"),
        required_float(row, "pitch"),
        required_float(row, "yaw"),
        required_float(row, "roll"),
    )
    data += struct.pack("<I", required_int(row, "entity_flags") & 0xFFFFFFFF)
    data += struct.pack("<BBBB", required_int(row, "move_type") & 0xFF, 0, 0, 0)
    data += struct.pack(
        "<QQQ",
        required_int(row, "buttons") & 0xFFFFFFFFFFFFFFFF,
        required_int(row, "buttons1") & 0xFFFFFFFFFFFFFFFF,
        required_int(row, "buttons2") & 0xFFFFFFFFFFFFFFFF,
    )
    data += struct.pack(
        "<ff",
        required_float(row, "duck_amount"),
        required_float(row, "duck_speed"),
    )
    data += struct.pack(
        "<fff",
        required_float(row, "ladder_normal_x"),
        required_float(row, "ladder_normal_y"),
        required_float(row, "ladder_normal_z"),
    )
    data += struct.pack(
        "<BBBB",
        required_int(row, "ducked") & 0xFF,
        required_int(row, "ducking") & 0xFF,
        required_int(row, "desires_duck") & 0xFF,
        required_int(row, "actual_move_type") & 0xFF,
    )
    if len(data) != 92:
        raise AssertionError(f"MovementSnapshot size is {len(data)}, expected 92")
    return bytes(data)


def sampled_transform_indexes(snapshot_count: int, stride: int) -> list[int]:
    if snapshot_count <= 0:
        return []
    stride = max(1, int(stride or 1))
    indexes = list(range(0, snapshot_count, stride))
    last_index = snapshot_count - 1
    if indexes[-1] != last_index:
        indexes.append(last_index)
    return indexes


def snapshot_origin_bytes(snapshot: bytes) -> bytes:
    if len(snapshot) != 92:
        raise AssertionError(f"MovementSnapshot size is {len(snapshot)}, expected 92")
    return snapshot[0:12]


def snapshot_angle_bytes(snapshot: bytes) -> bytes:
    if len(snapshot) != 92:
        raise AssertionError(f"MovementSnapshot size is {len(snapshot)}, expected 92")
    return snapshot[24:36]


def snapshot_velocity_bytes(snapshot: bytes) -> bytes:
    if len(snapshot) != 92:
        raise AssertionError(f"MovementSnapshot size is {len(snapshot)}, expected 92")
    return snapshot[12:24]


def write_varuint(output: bytearray, value: int) -> None:
    value = int(value)
    if value < 0:
        raise ValueError(f"negative varuint: {value}")
    while value >= 0x80:
        output.append((value & 0x7F) | 0x80)
        value >>= 7
    output.append(value & 0x7F)


def write_varint(output: bytearray, value: int) -> None:
    value = int(value)
    write_varuint(output, (value << 1) ^ (value >> 31))


def write_rle_varints(output: bytearray, values: list[int], *, signed: bool) -> None:
    index = 0
    while index < len(values):
        value = int(values[index])
        run = 1
        while index + run < len(values) and int(values[index + run]) == value:
            run += 1
        if signed:
            write_varint(output, value)
        else:
            write_varuint(output, value)
        write_varuint(output, run)
        index += run


def write_u64_rle(output: bytearray, values: list[int]) -> None:
    index = 0
    while index < len(values):
        value = int(values[index]) & 0xFFFFFFFFFFFFFFFF
        run = 1
        while index + run < len(values) and (int(values[index + run]) & 0xFFFFFFFFFFFFFFFF) == value:
            run += 1
        output += struct.pack("<Q", value)
        write_varuint(output, run)
        index += run


def write_sparse_varuint_override_rle(output: bytearray, values: list[int], defaults: list[int]) -> None:
    if len(values) != len(defaults):
        raise AssertionError(f"override length mismatch: {len(values)} values, {len(defaults)} defaults")

    runs: list[tuple[int, int, int]] = []
    index = 0
    while index < len(values):
        value = int(values[index])
        if value == int(defaults[index]):
            index += 1
            continue

        run = 1
        while (
            index + run < len(values)
            and int(values[index + run]) == value
            and int(values[index + run]) != int(defaults[index + run])
        ):
            run += 1
        runs.append((index, run, value))
        index += run

    write_varuint(output, len(runs))
    for start, run, value in runs:
        write_varuint(output, start)
        write_varuint(output, run)
        write_varuint(output, value)


def write_float_rle(output: bytearray, values: list[bytes]) -> None:
    runs: list[tuple[bytes, int]] = []
    index = 0
    while index < len(values):
        value = values[index]
        run = 1
        while index + run < len(values) and values[index + run] == value:
            run += 1
        runs.append((value, run))
        index += run

    write_varuint(output, len(runs))
    for value, run in runs:
        if len(value) != 4:
            raise AssertionError(f"float rle value has {len(value)} bytes, expected 4")
        output += value
        write_varuint(output, run)


def write_sparse_u64_rle(output: bytearray, values: list[int]) -> None:
    runs: list[tuple[int, int, int]] = []
    index = 0
    while index < len(values):
        value = int(values[index]) & 0xFFFFFFFFFFFFFFFF
        if value == 0:
            index += 1
            continue
        run = 1
        while index + run < len(values) and (int(values[index + run]) & 0xFFFFFFFFFFFFFFFF) == value:
            run += 1
        runs.append((index, run, value))
        index += run

    write_varuint(output, len(runs))
    for start, run, value in runs:
        write_varuint(output, start)
        write_varuint(output, run)
        output += struct.pack("<Q", value)


def write_sparse_vec3_rle(output: bytearray, values: list[bytes]) -> None:
    zero = b"\x00" * 12
    runs: list[tuple[int, int, bytes]] = []
    index = 0
    while index < len(values):
        value = values[index]
        if len(value) != 12:
            raise AssertionError(f"vec3 rle value has {len(value)} bytes, expected 12")
        if value == zero:
            index += 1
            continue
        run = 1
        while index + run < len(values) and values[index + run] == value:
            run += 1
        runs.append((index, run, value))
        index += run

    write_varuint(output, len(runs))
    for start, run, value in runs:
        write_varuint(output, start)
        write_varuint(output, run)
        output += value


def compact_subtick_bytes(subtick: bytes) -> bytes:
    if len(subtick) != 28:
        raise AssertionError(f"SubtickMove size is {len(subtick)}, expected 28")

    optional_flags = 0
    pressed = subtick[8:12]
    analog_forward = subtick[12:16]
    analog_left = subtick[16:20]
    pitch_delta = subtick[20:24]
    yaw_delta = subtick[24:28]

    if pressed != b"\x00" * 4:
        optional_flags |= 1 << 0
    if analog_forward != b"\x00" * 4:
        optional_flags |= 1 << 1
    if analog_left != b"\x00" * 4:
        optional_flags |= 1 << 2
    if pitch_delta != b"\x00" * 4:
        optional_flags |= 1 << 3
    if yaw_delta != b"\x00" * 4:
        optional_flags |= 1 << 4

    data = bytearray()
    data.append(optional_flags)
    data += subtick[0:8]  # when + button
    if optional_flags & (1 << 0):
        data += pressed
    if optional_flags & (1 << 1):
        data += analog_forward
    if optional_flags & (1 << 2):
        data += analog_left
    if optional_flags & (1 << 3):
        data += pitch_delta
    if optional_flags & (1 << 4):
        data += yaw_delta
    return bytes(data)


def subtick_bytes(row: Any) -> bytes:
    return struct.pack(
        "<fIfffff",
        float(row_value(row, "when_fraction", row_value(row, "when", 0.0))),
        int(row_value(row, "button", 0)) & 0xFFFFFFFF,
        float(row_value(row, "pressed", 0.0)),
        float(row_value(row, "analog_forward", row_value(row, "analogForward", 0.0))),
        float(row_value(row, "analog_left", row_value(row, "analogLeft", 0.0))),
        float(row_value(row, "pitch_delta", row_value(row, "pitchDelta", 0.0))),
        float(row_value(row, "yaw_delta", row_value(row, "yawDelta", 0.0))),
    )


def write_rec_string(output: Any, value: str) -> None:
    encoded = str(value or "").encode("utf-8")
    if len(encoded) > 0xFFFF:
        encoded = encoded[:0xFFFF]
    data = struct.pack("<H", len(encoded)) + encoded
    if isinstance(output, bytearray):
        output += data
    else:
        output.write(data)


def manifest_frame(row: Any, *, time_seconds: float) -> dict[str, Any]:
    payload = {
        "relativeTick": int(row["relative_tick"]),
        "time": rounded_float(time_seconds, 4),
        "x": rounded_float(row["x"]),
        "y": rounded_float(row["y"]),
        "z": rounded_float(row["z"]),
        "pitch": rounded_float(row["pitch"]),
        "yaw": rounded_float(row["yaw"]),
        "buttons": int(row["buttons"]),
        "activeWeapon": row["active_weapon"],
    }
    weapon_def = row_weapon_def(row)
    if weapon_def >= 0:
        payload["activeWeaponDefIndex"] = weapon_def
    return payload


def row_weapon_def(row: Any) -> int:
    explicit = optional_int(row, "active_weapon_def_index")
    if explicit is not None:
        return normalize_weapon_def_index(explicit)
    return weapon_def_index(row_value(row, "active_weapon", ""))


def weapon_def_index(item_name: Any) -> int:
    raw = str(item_name or "").lower()
    if "knife" in raw or "bayonet" in raw:
        return 42
    normalized = normalize_item(item_name)
    if not normalized:
        return -1
    lowered = normalized.lower()
    if "knife" in lowered or "bayonet" in lowered:
        return 42
    return WEAPON_DEF_INDEXES.get(normalized, -1)


def normalize_weapon_def_index(def_index: int | None) -> int:
    if def_index is None:
        return -1
    value = int(def_index)
    if value in (42, 59, 9001) or 500 <= value < 600:
        return 42
    return value


def is_preload_weapon_def(def_index: int) -> bool:
    normalized = normalize_weapon_def_index(def_index)
    if normalized < 0 or normalized in (31, 42, 49):
        return False
    preload_defs = {
        weapon_def_index(item)
        for item in PRIMARY_WEAPONS.union(SECONDARY_WEAPONS).union(UTILITY_ITEMS - {"weapon_taser"})
    }
    return normalized in preload_defs


def steamid_u64(value: str) -> int:
    text = steamid_text(value)
    return int(text) if text else 0


def first_frame_index_at_or_after(rows: list[Any], tick: int) -> int | None:
    for index, row in enumerate(rows):
        if int(row["tick"]) >= tick:
            return index
    return None


def relative_manifest_path(path: Path, manifest_dir: Path) -> str:
    try:
        return path.relative_to(manifest_dir).as_posix()
    except ValueError:
        return path.as_posix()






def collect_source_paths(values: Iterable[str]) -> list[Path]:
    source_paths: list[Path] = []
    for value in values:
        path = Path(value)
        if path.is_dir():
            source_paths.extend(child for child in path.rglob("*") if child.suffix.lower() in SUPPORTED_SOURCE_SUFFIXES)
        elif path.suffix.lower() in SUPPORTED_SOURCE_SUFFIXES and path.exists():
            source_paths.append(path)
    return sorted(set(source_paths))


def collect_demo_tasks(sources: list[Path], args: argparse.Namespace) -> list[dict[str, str]]:
    tasks: list[dict[str, str]] = []
    for source_path in sources:
        if source_path.suffix.lower() == ".dem":
            tasks.append({"path": str(source_path), "source_label": str(source_path)})
            continue

        try:
            members = [member for member in list_archive_members(source_path) if member.lower().endswith(".dem")]
        except Exception as error:
            if args.strict:
                raise
            print(f"Skipping {source_path}: {error}")
            continue

        for member in sorted(members):
            tasks.append(
                {
                    "archive": str(source_path),
                    "member": member,
                    "source_label": f"{source_path}::{member}",
                }
            )
    return tasks


def export_demo_task(task: dict[str, str], options: dict[str, Any]) -> dict[str, Any]:
    args = argparse.Namespace(**options)
    cleanup_dir: Path | None = None
    demo_path: Path
    source_label = task.get("source_label", task.get("path", ""))

    try:
        if "archive" in task:
            archive_path = Path(task["archive"])
            member = task["member"]
            if args.keep_extracted:
                extraction_dir = Path(args.work_dir) / sanitize_filename(archive_path.stem) / sanitize_filename(Path(member).stem)
                extraction_dir.mkdir(parents=True, exist_ok=True)
            else:
                Path(args.work_dir).mkdir(parents=True, exist_ok=True)
                extraction_dir = Path(tempfile.mkdtemp(prefix=f"{sanitize_filename(archive_path.stem)}_", dir=args.work_dir))
                cleanup_dir = extraction_dir
            demo_path = extract_archive_member(archive_path, member, extraction_dir)
        else:
            demo_path = Path(task["path"])

        map_name, rounds = export_direct_demo(demo_path, args, source_label)
        return {
            "ok": True,
            "source": source_label,
            "mapName": map_name,
            "rounds": rounds,
        }
    except Exception as error:
        if args.strict:
            raise
        return {
            "ok": False,
            "source": source_label,
            "error": str(error),
        }
    finally:
        if cleanup_dir is not None and not args.keep_extracted:
            shutil.rmtree(cleanup_dir, ignore_errors=True)


def consume_export_result(
    result: dict[str, Any],
    rounds_by_map: dict[str, list[dict[str, Any]]],
    parsed_by_map: dict[str, int],
) -> int:
    if not result.get("ok"):
        print(f"Skipping {result.get('source', '<unknown>')}: {result.get('error', 'unknown error')}")
        return 1

    map_name = str(result.get("mapName") or "")
    rounds = result.get("rounds") or []
    if not map_name or not rounds:
        return 0

    rounds_by_map.setdefault(map_name, []).extend(rounds)
    parsed_by_map[map_name] = parsed_by_map.get(map_name, 0) + 1
    print(f"Extracted {len(rounds)} rounds from {result.get('source')} ({map_name})")
    return 0


def resolve_manifest_path(args: argparse.Namespace, map_name: str) -> Path:
    template = str(args.export_manifest)
    if "{map}" in template:
        return Path(template.format(map=map_name))
    if args.map:
        return Path(template)
    base = Path(template)
    return base.with_name(f"{map_name}_openings_manifest.json")


def resolve_records_dir(args: argparse.Namespace, manifest_path: Path, map_name: str) -> Path:
    if args.records_dir:
        text = str(args.records_dir)
        record_root = Path(text.format(map=map_name) if "{map}" in text else text)
    else:
        record_root = manifest_path.with_name(f"{manifest_path.stem}_records")
    if not record_root.is_absolute():
        record_root = manifest_path.parent / record_root
    return record_root


def reset_export_outputs(args: argparse.Namespace) -> None:
    if args.map:
        manifest_path = resolve_manifest_path(args, args.map)
        manifest_path.unlink(missing_ok=True)
        shutil.rmtree(resolve_records_dir(args, manifest_path, args.map), ignore_errors=True)
        return

    template = Path(str(args.export_manifest).format(map="de_dust2"))
    base = template.parent
    if not str(base):
        base = Path(".")
    for manifest in base.glob("*_openings_manifest.json"):
        manifest.unlink(missing_ok=True)
    for records_dir in base.glob("*_openings_manifest_records"):
        if records_dir.is_dir():
            shutil.rmtree(records_dir, ignore_errors=True)


def iter_archive_demos(archive_path: Path, args: argparse.Namespace, existing_demo_labels: set[str] | None = None):
    try:
        members = [member for member in list_archive_members(archive_path) if member.lower().endswith(".dem")]
    except Exception as error:
        if args.strict:
            raise
        print(f"Skipping {archive_path}: {error}")
        return

    if not members:
        return

    selected_members = members

    for member in selected_members:
        source_label = f"{archive_path}::{member}"
        if existing_demo_labels and source_label in existing_demo_labels:
            print(f"Skipping existing {source_label}")
            continue

        cleanup_dir = None
        if args.keep_extracted:
            extraction_dir = Path(args.work_dir) / sanitize_filename(archive_path.stem) / sanitize_filename(Path(member).stem)
            extraction_dir.mkdir(parents=True, exist_ok=True)
        else:
            Path(args.work_dir).mkdir(parents=True, exist_ok=True)
            extraction_dir = Path(tempfile.mkdtemp(prefix=f"{sanitize_filename(archive_path.stem)}_", dir=args.work_dir))
            cleanup_dir = extraction_dir

        try:
            extracted_path = extract_archive_member(archive_path, member, extraction_dir)
        except Exception as error:
            if cleanup_dir is not None and not args.keep_extracted:
                shutil.rmtree(cleanup_dir, ignore_errors=True)
            if args.strict:
                raise
            print(f"Skipping {archive_path}::{member}: {error}")
            continue

        yield extracted_path, source_label, cleanup_dir


def list_archive_members(archive_path: Path) -> list[str]:
    seven_zip = shutil.which("7z")
    errors = []
    if seven_zip:
        result = subprocess.run([seven_zip, "l", "-slt", str(archive_path)], check=False, capture_output=True, text=True)
        if result.returncode == 0:
            return parse_7z_member_listing(result.stdout, archive_path)
        errors.append(compact_subprocess_error("7z", result))

    unrar = shutil.which("unrar")
    if archive_path.suffix.lower() == ".rar" and unrar:
        result = subprocess.run([unrar, "lb", str(archive_path)], check=False, capture_output=True, text=True)
        if result.returncode == 0:
            return [line.strip() for line in result.stdout.splitlines() if line.strip()]
        errors.append(compact_subprocess_error("unrar", result))

    if not errors:
        raise RuntimeError(f"Need 7z or unrar to inspect {archive_path}")
    raise RuntimeError(f"could not inspect archive members ({'; '.join(errors)})")


def parse_7z_member_listing(output: str, archive_path: Path) -> list[str]:
    members = []
    for line in output.splitlines():
        if line.startswith("Path = "):
            value = line.removeprefix("Path = ").strip()
            if value and value != str(archive_path):
                members.append(value)
    return members


def compact_subprocess_error(tool_name: str, result: subprocess.CompletedProcess[str]) -> str:
    output = (result.stderr or result.stdout).strip().splitlines()
    detail = output[-1].strip() if output else f"exit {result.returncode}"
    return f"{tool_name}: {detail}"


def extract_archive_member(archive_path: Path, member: str, output_dir: Path) -> Path:
    before = {path.resolve() for path in output_dir.rglob("*.dem")}
    if archive_path.suffix.lower() == ".rar" and shutil.which("unrar"):
        subprocess.run([shutil.which("unrar") or "unrar", "e", "-y", str(archive_path), member, str(output_dir) + "/"], check=True, stdout=subprocess.DEVNULL)
    else:
        seven_zip = shutil.which("7z")
        if not seven_zip:
            raise RuntimeError(f"Need unrar or 7z to extract {archive_path}")
        subprocess.run([seven_zip, "x", "-y", f"-o{output_dir}", str(archive_path), member], check=True, stdout=subprocess.DEVNULL)
    after = [path for path in output_dir.rglob("*.dem") if path.resolve() not in before]
    if after:
        return after[0]
    fallback = output_dir / Path(member).name
    if fallback.exists():
        return fallback
    candidates = list(output_dir.rglob(Path(member).name))
    if candidates:
        return candidates[0]
    raise RuntimeError(f"archive extractor did not produce {member!r} from {archive_path}")


def normalize_inventory(value: Any) -> list[str]:
    if value is None:
        return []
    if isinstance(value, str):
        try:
            parsed_value = json.loads(value.replace("'", '"'))
            if isinstance(parsed_value, list):
                return [normalize_item(item) for item in parsed_value if normalize_item(item)]
        except json.JSONDecodeError:
            value = [item.strip() for item in value.strip("[]").split(",")]
    if isinstance(value, (list, tuple, set)):
        return [normalized for item in value if (normalized := normalize_item(item))]
    return []


def summarize_equipment(rows: Any) -> dict[str, int]:
    primary_value = 0
    utility_value = 0
    armor_value = 0
    total_value = 0

    for _, row in rows.iterrows():
        inventory = normalize_inventory(row.get("inventory", []))
        for item_name in inventory:
            price = item_price(item_name)
            total_value += price
            if item_name in PRIMARY_WEAPONS:
                primary_value += price
            elif item_name in UTILITY_ITEMS:
                utility_value += price

        armor = int(row.get("armor_value", 0) or 0)
        if armor > 0:
            armor_price = 1000 if bool(row.get("has_helmet", False)) else 650
            armor_value += armor_price
            total_value += armor_price
        if bool(row.get("has_defuser", False)):
            total_value += ITEM_PRICES["item_defuser"]

    return {
        "primaryValue": primary_value,
        "utilityValue": utility_value,
        "armorValue": armor_value,
        "totalValue": total_value,
    }


def normalize_item(value: Any) -> str:
    if value is None:
        return ""
    text = str(value).strip().strip("'").strip('"')
    if not text or text.lower() == "nan":
        return ""
    if text.lower() in {"weapon_c4", "c4", "bomb"}:
        return ""
    if text.startswith("weapon_") or text.startswith("item_"):
        return text
    lowered = text.lower().replace("_", " ")
    if lowered.startswith("knife") or lowered in {"c4", "bomb"}:
        return ""
    if lowered in WEAPON_ALIASES:
        return WEAPON_ALIASES[lowered]
    candidate = "weapon_" + re.sub(r"[^a-z0-9]+", "_", lowered).strip("_")
    return candidate if candidate != "weapon_" else ""


def item_price(item_name: str) -> int:
    return ITEM_PRICES.get(item_name, 0)


def normalize_grenade_type(value: Any) -> str:
    text = str(value or "")
    return PROJECTILE_TO_WEAPON.get(text, text)


def map_matches(expected: str, actual: str) -> bool:
    expected_values = MAP_ALIASES.get(expected.lower(), {expected.lower()})
    return actual.lower() in expected_values


def canonical_map_name(expected: str, actual: str) -> str:
    if expected and map_matches(expected, actual):
        return expected
    return actual


def member_name_matches_map(map_name: str, member: str) -> bool:
    lowered = Path(member).name.lower()
    aliases = MAP_ALIASES.get(map_name.lower(), {map_name.lower()})
    return any(alias in lowered for alias in aliases)


def demo_stem(demo_label: str) -> str:
    member = demo_label.split("::", 1)[-1]
    return sanitize_filename(Path(member).stem)


def source_fingerprint(source_label: str) -> str:
    return hashlib.blake2s(source_label.encode("utf-8", errors="ignore"), digest_size=5).hexdigest()


def round_output_key(source_label: str, round_number: int) -> str:
    return sanitize_filename(f"{demo_stem(source_label)}_{source_fingerprint(source_label)}_r{round_number}")


def demo_output_key(source_label: str) -> str:
    return sanitize_filename(f"{demo_stem(source_label)}_{source_fingerprint(source_label)}")




def is_missing(value: Any) -> bool:
    if value is None:
        return True
    try:
        return bool(math.isnan(value))
    except (TypeError, ValueError):
        return False


def row_value(row: Any, name: str, default: Any = None) -> Any:
    if isinstance(row, dict):
        return row.get(name, default)
    keys = row.keys() if hasattr(row, "keys") else []
    return row[name] if name in keys else default


def required_value(row: Any, name: str) -> Any:
    value = row_value(row, name)
    if is_missing(value):
        raise RuntimeError(f"missing required recorder field {name}")
    return value


def required_float(row: Any, name: str) -> float:
    value = required_value(row, name)
    return float(value)


def required_int(row: Any, name: str) -> int:
    value = required_value(row, name)
    if isinstance(value, bool):
        return int(value)
    return int(float(value))


def required_vector(row: Any, name: str, size: int) -> list[float]:
    value = required_value(row, name)
    if not isinstance(value, (list, tuple)) or len(value) < size:
        raise RuntimeError(f"required recorder field {name} is not a {size}-component vector")
    return [float(value[index]) for index in range(size)]


def optional_float(row: Any, name: str) -> float | None:
    value = row_value(row, name)
    if is_missing(value):
        return None
    return float(value)


def optional_int(row: Any, name: str) -> int | None:
    value = row_value(row, name)
    if is_missing(value):
        return None
    return int(float(value))


def finite_float(value: Any) -> float | None:
    if is_missing(value):
        return None
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        return None
    return parsed if math.isfinite(parsed) else None


def first_finite_float(row: Any, *names: str, default: float = 0.0) -> float:
    for name in names:
        parsed = finite_float(row_value(row, name))
        if parsed is not None:
            return parsed
    return float(default)


def first_int(row: Any, *names: str, default: int = 0) -> int:
    for name in names:
        parsed = optional_int(row, name)
        if parsed is not None:
            return parsed
    return int(default)


def normalize_inventory_def_indexes(value: Any) -> list[int]:
    if value is None:
        return []
    if isinstance(value, str):
        try:
            value = json.loads(value.replace("'", '"'))
        except json.JSONDecodeError:
            value = [item.strip() for item in value.strip("[]").split(",")]
    if isinstance(value, (list, tuple, set)):
        out = []
        for item in value:
            if is_missing(item):
                continue
            try:
                normalized = normalize_weapon_def_index(int(float(item)))
            except (TypeError, ValueError):
                continue
            if normalized >= 0:
                out.append(normalized)
        return out
    return []


def optional_vector(row: Any, name: str, size: int) -> list[float] | None:
    value = row_value(row, name)
    if is_missing(value) or not isinstance(value, (list, tuple)) or len(value) < size:
        return None
    output = []
    for index in range(size):
        parsed = finite_float(value[index])
        if parsed is None:
            return None
        output.append(parsed)
    return output


def inferred_entity_flags(row: Any) -> int:
    airborne = row_value(row, "is_airborne")
    if isinstance(airborne, bool):
        return 0 if airborne else 1
    if not is_missing(airborne):
        try:
            return 0 if bool(int(float(airborne))) else 1
        except (TypeError, ValueError):
            pass
    return 1


def build_replay_subticks(row: dict[str, Any]) -> list[dict[str, float | int]]:
    raw_moves = row_value(row, "usercmd_subtick_moves")
    if isinstance(raw_moves, list) and raw_moves:
        output = []
        for move in raw_moves:
            if not isinstance(move, dict):
                continue
            parsed = {
                "when": max(0.0, min(0.999999, first_finite_float(move, "when", default=0.0))),
                "button": first_int(move, "button", default=0),
                "pressed": first_finite_float(move, "pressed", default=0.0),
                "analogForward": first_finite_float(
                    move,
                    "analog_forward_delta",
                    "analog_forward",
                    "analogForward",
                    default=0.0,
                ),
                "analogLeft": first_finite_float(
                    move,
                    "analog_left_delta",
                    "analog_left",
                    "analogLeft",
                    default=0.0,
                ),
                "pitchDelta": first_finite_float(move, "pitch_delta", "pitchDelta", default=0.0),
                "yawDelta": first_finite_float(move, "yaw_delta", "yawDelta", default=0.0),
            }
            if not is_noop_subtick_move(parsed):
                output.append(parsed)
        return sorted(output, key=lambda item: float(item["when"]))

    history = row_value(row, "usercmd_input_history")
    if not isinstance(history, list) or not history:
        return []

    prev_pitch = first_finite_float(row, "usercmd_viewangle_x", "pitch", default=0.0)
    prev_yaw = first_finite_float(row, "usercmd_viewangle_y", "yaw", default=0.0)
    output: list[dict[str, float | int]] = []

    def fraction(entry: Any) -> float:
        if not isinstance(entry, dict):
            return 0.0
        value = entry.get("player_tick_fraction", entry.get("render_tick_fraction", 0.0))
        try:
            return max(0.0, min(0.999999, float(value)))
        except (TypeError, ValueError):
            return 0.0

    for entry in sorted((item for item in history if isinstance(item, dict)), key=fraction):
        pitch = entry.get("x")
        yaw = entry.get("y")
        if is_missing(pitch) or is_missing(yaw):
            continue
        pitch = float(pitch)
        yaw = float(yaw)
        pitch_delta = pitch - prev_pitch
        yaw_delta = angle_delta(prev_yaw, yaw)
        prev_pitch = pitch
        prev_yaw = yaw
        if abs(pitch_delta) < 0.000001 and abs(yaw_delta) < 0.000001:
            continue
        output.append(
            {
                "when": fraction(entry),
                "button": 0,
                "pressed": 0.0,
                "analogForward": 0.0,
                "analogLeft": 0.0,
                "pitchDelta": pitch_delta,
                "yawDelta": yaw_delta,
            }
        )
    return output


def is_noop_subtick_move(move: dict[str, float | int]) -> bool:
    if int(move.get("button", 0) or 0) != 0:
        return False
    return (
        abs(float(move.get("pressed", 0.0) or 0.0)) < 0.000001
        and abs(float(move.get("analogForward", 0.0) or 0.0)) < 0.000001
        and abs(float(move.get("analogLeft", 0.0) or 0.0)) < 0.000001
        and abs(float(move.get("pitchDelta", 0.0) or 0.0)) < 0.000001
        and abs(float(move.get("yawDelta", 0.0) or 0.0)) < 0.000001
    )


def angle_delta(start: float, end: float) -> float:
    return ((end - start) % 360.0 + 540.0) % 360.0 - 180.0


def nearest_player_frame(round_rows: Any, steamid: str, tick: int) -> Any | None:
    if round_rows.empty:
        return None
    column = steamid_column(round_rows)
    player_rows = round_rows[round_rows[column].map(steamid_text) == steamid]
    if player_rows.empty:
        return None
    player_rows = player_rows.assign(_distance=(player_rows["tick"] - tick).abs())
    return player_rows.sort_values("_distance").iloc[0]


def first_existing_column(rows: Any, *names: str) -> str | None:
    for name in names:
        if name in rows:
            return name
    return None


def handle_extract_error(args: argparse.Namespace, source_label: str, error: Exception) -> None:
    if args.strict:
        raise error
    print(f"Skipping {source_label}: {error}")


def steamid_column(rows: Any) -> str:
    if "steamid" in rows:
        return "steamid"
    if "player_steamid" in rows:
        return "player_steamid"
    raise KeyError("No steamid/player_steamid column in tick data")


def steamid_text(value: Any) -> str:
    if is_missing(value):
        return ""
    if isinstance(value, str):
        text = value.strip()
        if not text or text.lower() in {"nan", "none", "undefined"}:
            return ""
        if re.fullmatch(r"\d+", text):
            return text
        try:
            parsed = int(float(text))
            return str(parsed) if parsed > 0 else ""
        except ValueError:
            return ""
    try:
        parsed = int(value)
    except (TypeError, ValueError, OverflowError):
        return ""
    return str(parsed) if parsed > 0 else ""


def safe_sum(rows: Any, column: str) -> int:
    if column not in rows:
        return 0
    return int(rows[column].fillna(0).astype(float).sum())


def rounded_float(value: Any, digits: int = 3) -> float:
    return round(float(value or 0.0), digits)


def first_text(rows: Any, column: str, fallback: str) -> str:
    if column not in rows:
        return fallback
    values = [str(value) for value in rows[column].dropna().unique() if str(value)]
    return values[0] if values else fallback


def team_label(team_num: int) -> str:
    return "T" if team_num == 2 else "CT" if team_num == 3 else str(team_num)


def unique(values: Iterable[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for value in values:
        if value in seen:
            continue
        seen.add(value)
        result.append(value)
    return result


def sanitize_filename(value: str) -> str:
    return re.sub(r"[^a-zA-Z0-9._-]+", "_", value)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
