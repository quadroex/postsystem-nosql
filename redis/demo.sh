#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
prefix="postsystem:demo:$(date +%s):$$"
first="$prefix:PSDEMO000001"
second="$prefix:PSDEMO000002"
expiring="$prefix:PSDEMO000003"

redis() {
    docker compose --project-directory "$root" -f "$root/compose.yaml" exec -T redis redis-cli --raw "$@"
}

check() {
    if [ "$1" != "$2" ]; then
        printf 'Expected [%s], got [%s]\n' "$2" "$1" >&2
        exit 1
    fi
}

trap 'redis DEL "$first" "$second" "$expiring" >/dev/null 2>&1 || true' EXIT

reply=$(redis PING)
check "$reply" PONG
printf 'PING = %s\n' "$reply"

reply=$(redis SET "$first" accepted EX 60)
check "$reply" OK
printf 'SET PSDEMO000001 accepted EX 60 = %s\n' "$reply"

reply=$(redis SET "$second" arrived EX 60)
check "$reply" OK
printf 'SET PSDEMO000002 arrived EX 60 = %s\n' "$reply"

status=$(redis GET "$first")
check "$status" accepted
printf 'GET PSDEMO000001 = %s\n' "$status"

statuses=$(redis MGET "$first" "$second")
check "$statuses" "$(printf '%s\n%s' accepted arrived)"
printf 'MGET PSDEMO000001 PSDEMO000002\n%s\n' "$statuses"

reply=$(redis SET "$first" in_transit XX EX 60)
check "$reply" OK
status=$(redis GET "$first")
check "$status" in_transit
printf 'SET PSDEMO000001 in_transit XX EX 60 = %s\n' "$reply"
printf 'GET PSDEMO000001 = %s\n' "$status"

ttl=$(redis TTL "$first")
[ "$ttl" -gt 0 ] && [ "$ttl" -le 60 ] || exit 1
printf 'TTL PSDEMO000001 = %s seconds\n' "$ttl"

deleted=$(redis DEL "$second")
check "$deleted" 1
check "$(redis GET "$second")" ''
printf 'DEL PSDEMO000002 = %s\n' "$deleted"
printf 'GET PSDEMO000002 = cache miss\n'

reply=$(redis SET "$expiring" draft EX 3)
check "$reply" OK
status=$(redis GET "$expiring")
check "$status" draft
printf 'SET PSDEMO000003 draft EX 3 = %s\n' "$reply"
printf 'GET PSDEMO000003 before expiry = %s\n' "$status"
sleep 4
check "$(redis GET "$expiring")" ''
ttl=$(redis TTL "$expiring")
check "$ttl" -2
printf 'GET PSDEMO000003 after expiry = cache miss\n'
printf 'TTL PSDEMO000003 after expiry = %s\n' "$ttl"
