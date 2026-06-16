"""
Batch-extract HLTV demo IDs from match pages using Playwright browser.
Usage: python3 scrape_demo_ids.py <match_paths_file> <output_ids_file>
"""
import sys
import time

def main():
    if len(sys.argv) < 3:
        print("Usage: python3 scrape_demo_ids.py <match_paths_file> <output_ids_file>")
        sys.exit(1)

    match_paths_file = sys.argv[1]
    output_file = sys.argv[2]

    with open(match_paths_file) as f:
        paths = [line.strip() for line in f if line.strip()]

    print(f"Processing {len(paths)} match pages...")

    from playwright.sync_api import sync_playwright
    import re

    demo_ids = []
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()

        for i, path in enumerate(paths):
            url = f"https://www.hltv.org{path}"
            try:
                page.goto(url, timeout=30000, wait_until="domcontentloaded")
                content = page.content()
                ids = re.findall(r'/download/demo/(\d+)', content)
                for did in ids:
                    if did not in demo_ids:
                        demo_ids.append(did)
                        print(f"  [{i+1}/{len(paths)}] {path} -> demo {did}")
                if not ids:
                    print(f"  [{i+1}/{len(paths)}] {path} -> NO DEMO FOUND")
                time.sleep(1.5)
            except Exception as e:
                print(f"  [{i+1}/{len(paths)}] {path} -> ERROR: {e}")
                time.sleep(3)

        browser.close()

    with open(output_file, 'w') as f:
        for did in demo_ids:
            f.write(f"{did}\n")

    print(f"\nDone! {len(demo_ids)} demo IDs saved to {output_file}")

if __name__ == "__main__":
    main()
