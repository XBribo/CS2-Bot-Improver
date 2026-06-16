#!/usr/bin/env python3
"""
BFS-scrape SteamID64s via Steam Web API GetFriendList.

Usage:
  STEAM_API_KEY=xxx python scrape_steamids.py \
      --seed 76561197960287930 \
      --target 1000 \
      --out /data2/bill/cs2/game/csgo/addons/counterstrikesharp/plugins/BotRandomizer/steamids.txt
"""
import argparse
import os
import sys
import time
from collections import deque

import requests

API = "https://api.steampowered.com/ISteamUser/GetFriendList/v1/"


def get_friends(key: str, sid: int, session: requests.Session, retries: int = 3):
    """Return list of SteamID64 friends, or [] if private/unavailable."""
    params = {"key": key, "steamid": sid, "relationship": "friend"}
    for attempt in range(retries):
        try:
            r = session.get(API, params=params, timeout=15)
        except requests.RequestException as e:
            print(f"  [warn] {sid}: {e}", file=sys.stderr)
            time.sleep(2 ** attempt)
            continue
        if r.status_code == 401:
            return []  # private friend list
        if r.status_code == 429:
            wait = 30 * (attempt + 1)
            print(f"  [rate-limited] sleep {wait}s", file=sys.stderr)
            time.sleep(wait)
            continue
        if r.status_code >= 500:
            time.sleep(2 ** attempt)
            continue
        if r.status_code == 200:
            try:
                fl = r.json().get("friendslist", {}).get("friends", [])
            except ValueError:
                return []
            return [int(f["steamid"]) for f in fl if "steamid" in f]
        return []
    return []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=76561197960287930)
    ap.add_argument("--target", type=int, default=1000)
    ap.add_argument("--out", required=True)
    ap.add_argument("--key", default=os.environ.get("STEAM_API_KEY"))
    ap.add_argument("--sleep", type=float, default=1.0,
                    help="seconds between API calls")
    args = ap.parse_args()

    if not args.key:
        sys.exit("Set --key or STEAM_API_KEY")

    session = requests.Session()
    visited: set[int] = set()        # IDs we've already queried
    collected: set[int] = {args.seed}  # SteamID64s in the output set
    queue = deque([args.seed])

    print(f"[start] seed={args.seed} target={args.target}")
    while queue and len(collected) < args.target:
        sid = queue.popleft()
        if sid in visited:
            continue
        visited.add(sid)
        friends = get_friends(args.key, sid, session)
        added = 0
        for f in friends:
            if f not in collected:
                collected.add(f)
                added += 1
                if len(collected) >= args.target:
                    break
            if f not in visited:
                queue.append(f)
        print(f"  {sid}: {len(friends):4d} friends, +{added} (total={len(collected)}, queued={len(queue)})")
        time.sleep(args.sleep)

    out_path = args.out
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w") as f:
        for sid in sorted(collected):
            f.write(f"{sid}\n")
    print(f"[done] wrote {len(collected)} ids -> {out_path}")


if __name__ == "__main__":
    main()
