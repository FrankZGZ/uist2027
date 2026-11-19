#!/bin/bash
# Starts the relay on a Mac for the two Quest headsets. Double-click in Finder, or run it in Terminal.
# Keep this window open for the whole session; closing it stops the relay.
cd "$(dirname "$0")/relay" || exit 1

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 not found. Run: xcode-select --install   (or install Python from python.org), then try again."
    read -r -p "Press Return to close." _
    exit 1
fi
if [ ! -f .env ] && [ -z "$OPENAI_API_KEY" ]; then
    echo "No relay/.env file: the relay will run without OpenAI (same-id panel mapping, no AI reading)."
    echo "Copy .env.example to .env and put your key in it to enable OpenAI."
    echo
fi

echo "This Mac's addresses (the headsets must be on the same Wi-Fi):"
ifconfig | awk '/inet / && $2 != "127.0.0.1" {print "  " $2}'
echo
exec python3 relay.py --listen 9000
