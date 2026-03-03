#!/bin/bash
# TSG — CopilotBoost for Linux (monitor mode only for now)
MODE="${1:-help}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

copilot_session_dir="$HOME/.copilot/session-state"

monitor() {
    while true; do
        clear
        echo -e "  ${CYAN}⚡ TSG MONITOR  $(date +%H:%M:%S)${NC}"
        echo "  ══════════════════════════════════════════════════════"

        local pids=$(pgrep -f "copilot" 2>/dev/null)
        if [ -z "$pids" ]; then
            echo -e "  ${RED}❌ No Copilot processes${NC}"
        else
            local total_ram=0
            for pid in $pids; do
                local name=$(ps -p "$pid" -o comm= 2>/dev/null)
                local ram=$(ps -p "$pid" -o rss= 2>/dev/null | awk '{printf "%.1f", $1/1024}')
                local cpu=$(ps -p "$pid" -o %cpu= 2>/dev/null)
                total_ram=$(echo "$total_ram + $ram" | bc 2>/dev/null || echo "$total_ram")
                echo -e "  PID ${pid}  | ${name:-?}  | ${ram:-0} MB | CPU: ${cpu:-0}%"
            done
            echo "  ══════════════════════════════════════════════════════"
            echo -e "  TOTAL: ${total_ram} MB"
        fi

        # Session diagnostics (READ-ONLY)
        if [ -d "$copilot_session_dir" ]; then
            local sessions=$(ls -d "$copilot_session_dir"/*/ 2>/dev/null | wc -l)
            local stuck=0
            for dir in "$copilot_session_dir"/*/; do
                local ev="$dir/events.jsonl"
                [ -f "$ev" ] || continue
                local last_type=$(tail -1 "$ev" 2>/dev/null | python3 -c "import sys,json; print(json.load(sys.stdin).get('type',''))" 2>/dev/null)
                [ "$last_type" = "assistant.turn_start" ] && ((stuck++))
            done
            echo -e "\n  📂 Sessions: $sessions | Stuck: $stuck"
        fi

        echo -e "  ⏱ Refresh: 5s | Ctrl+C stop"
        sleep 5
    done
}

status() {
    echo -e "\n  ${CYAN}📋 TSG Status${NC}\n"
    local count=$(pgrep -fc "copilot" 2>/dev/null || echo 0)
    echo "  Copilot processes: $count"
    if [ -d "$copilot_session_dir" ]; then
        echo "  Sessions: $(ls -d "$copilot_session_dir"/*/ 2>/dev/null | wc -l)"
    fi
}

case "$MODE" in
    monitor|-Monitor) monitor ;;
    status|-Status) status ;;
    *) echo "Usage: $0 {monitor|status}" ;;
esac
