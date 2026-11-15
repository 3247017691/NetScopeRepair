"""Read-only, bounded inspection for the DeepSeek construction plan; never extracts files.
Usage: python inspect_incident.py <incident.zip> <output.json>
Output excludes raw interface names, addresses, GUIDs and free-text errors.
"""
import collections
import datetime as dt
import hashlib
import json
from pathlib import Path, PurePosixPath
import sys
import zipfile

archive = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
if archive == output:
    raise ValueError('Output must not overwrite the archive')
with zipfile.ZipFile(archive) as z:
    entries = z.infolist()
    names = [i.filename for i in entries]
    if len(entries) > 4096 or len(set(names)) != len(names):
        raise ValueError('Too many or duplicate entries')
    for i in entries:
        p = PurePosixPath(i.filename)
        if p.is_absolute() or '..' in p.parts or '\\' in i.filename or ':' in i.filename:
            raise ValueError('Unsafe entry name')
        if i.file_size > 32 * 1024 * 1024 or i.flag_bits & 1:
            raise ValueError('Oversized or encrypted entry')
    if sum(i.file_size for i in entries) > 128 * 1024 * 1024:
        raise ValueError('Archive exceeds inspection budget')
    manifest = json.loads(z.read('manifest.json'))
    rows = [json.loads(line) for line in z.read('events.ndjson').decode('utf-8-sig').splitlines() if line.strip()]
    checksum_names = []
    bad = []
    for line in z.read('checksums.sha256').decode('utf-8-sig').splitlines():
        if not line.strip():
            continue
        expected, name = line.split(None, 1)
        name = name.lstrip('* ')
        checksum_names.append(name)
        if hashlib.sha256(z.read(name)).hexdigest() != expected:
            bad.append(name)
    expected_names = set(names) - {'checksums.sha256'}
    checksum_coverage = set(checksum_names) == expected_names and len(checksum_names) == len(expected_names)
    snapshots = [(r, r['payload']['snapshot']) for r in rows if r.get('event_type') == 'snapshot']
    timezone = dt.timezone(dt.timedelta(hours=8))
    def local(value):
        return dt.datetime.fromisoformat(value).astimezone(timezone).isoformat()
    def facts(item):
        return {k: item.get(k) for k in ('if_index', 'kind', 'operational_status', 'metric', 'automatic_metric', 'disable_default_routes', 'family')}
    changes = []
    previous = {}
    for r, s in snapshots:
        current = {i['interface_guid']: facts(i) for i in s['interfaces'].get('value', [])}
        delta = []
        if previous:
            for key, item in current.items():
                if key not in previous:
                    delta.append({'change': 'appeared_in_observation', 'after': item})
                elif item != previous[key]:
                    delta.append({'change': 'observed_fields_changed', 'before': previous[key], 'after': item})
            for key, item in previous.items():
                if key not in current:
                    delta.append({'change': 'absent_from_observation', 'before': item})
        if delta:
            changes.append({'sequence': r['sequence'], 'observed_at_local': local(r['observed_at']), 'changes': delta})
        previous = current
    timestamps = [dt.datetime.fromisoformat(r['observed_at']) for r in rows]
    deltas = [(b - a).total_seconds() for a, b in zip(timestamps, timestamps[1:])]
    text_errors = [error for _, s in snapshots for error in s['interfaces'].get('errors', [])]
    # This is a bounded heuristic finding, not a complete privacy audit.
    unaliased_error_count = sum(': an entry with neither an interface index nor an address was skipped.' in error
                                and not error.startswith('device-') for error in text_errors)
    children = ('tool_process_environment', 'user_environment', 'machine_environment', 'win_inet', 'win_http')
    child_statuses = {k: dict(collections.Counter(s['proxies'].get('value', {}).get(k, {}).get('status', 'absent') for _, s in snapshots)) for k in children}
    result = {
        'scope': 'Bounded read-only archive inspection, not product replay or original-topology validation',
        'archive_filename': archive.name,
        'archive_sha256': hashlib.sha256(archive.read_bytes()).hexdigest(),
        'entry_count': len(entries),
        'uncompressed_bytes': sum(i.file_size for i in entries),
        'checksums_checked': len(checksum_names),
        'checksum_mismatches': bad,
        'checksum_full_coverage': checksum_coverage,
        'manifest_counts': manifest.get('counts'),
        'manifest_app_version': manifest.get('app_version'),
        'manifest_rules_version': manifest.get('rules_version'),
        'time_zone': 'Asia/Shanghai (+08:00)',
        'started_at_local': local(rows[0]['observed_at']),
        'ended_at_local': local(rows[-1]['observed_at']),
        'duration_seconds': (timestamps[-1] - timestamps[0]).total_seconds(),
        'maximum_observation_spacing_seconds': max(deltas, default=0),
        'event_types': dict(collections.Counter(r.get('event_type') for r in rows)),
        'event_statuses': dict(collections.Counter(r.get('status') for r in rows)),
        'synthetic_event_count': sum(bool(r.get('synthetic')) for r in rows),
        'sequences_contiguous_from_one': [r['sequence'] for r in rows] == list(range(1, len(rows) + 1)),
        'duplicate_event_ids': len(rows) - len({r['event_id'] for r in rows}),
        'domain_status_counts': {k: dict(collections.Counter(s[k]['status'] for _, s in snapshots)) for k in ('interfaces', 'routes', 'dns', 'proxies')},
        'proxy_child_status_counts': child_statuses,
        'snapshot_field_names': sorted(snapshots[0][1]),
        'interface_observation_changes': changes,
        'potential_unaliased_interface_error_occurrences': unaliased_error_count,
        'limits': [
            'Checksums establish package self-consistency, not authenticity or absence of sensitive data.',
            'No probe events means this archive cannot establish external connectivity or application recovery.',
            'Observed parameter changes do not identify who changed them or prove causation.',
            'Interface disappearance may be absence in observation; no interface-deletion cause is proven.',
            'Address aliases do not preserve arbitrary prefixes; do not calculate live repair targets from this archive.',
            'Manifest gaps counts gap events; zero does not mean all collection domains are complete.',
            'Text-error count is heuristic; raw interface and virtual-switch names remain in some errors.',
        ],
    }
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({k: result[k] for k in ('entry_count', 'checksums_checked', 'checksum_mismatches', 'checksum_full_coverage', 'event_types', 'started_at_local', 'ended_at_local', 'maximum_observation_spacing_seconds', 'potential_unaliased_interface_error_occurrences')}, ensure_ascii=False))
