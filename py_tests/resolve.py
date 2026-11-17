import ipaddress
import sys
import dns.resolver

# Configurable DNS Resolvers
RESOLVERS = [
    "1.1.1.1",       # Cloudflare
    "8.8.8.8",       # Google
    "9.9.9.9",       # Quad9
    "208.67.222.222" # OpenDNS
]

def is_valid_ip(host: str) -> bool:
    """Checks if host is a valid IPv4 or IPv6 address."""
    try:
        ipaddress.ip_address(host)
        return True
    except ValueError:
        return False

def check_domain_resolves(domain: str, resolvers: list[str]) -> bool:
    """
    Attempts to resolve a domain against specified resolvers.
    Returns True if at least one resolver returns an answer (not NXDOMAIN/NoAnswer).
    """
    res = dns.resolver.Resolver()
    res.lifetime = 2.0  # Timeout per query in seconds

    for resolver_ip in resolvers:
        res.nameservers = [resolver_ip]
        try:
            # Query for standard A record (or AAAA/ANY if required)
            res.resolve(domain, 'A')
            return True
        except (dns.resolver.NXDOMAIN, dns.resolver.NoAnswer):
            continue
        except (dns.resolver.Timeout, dns.resolver.NoNameservers, Exception):
            continue

    return False

def process_targets(input_file: str, output_file: str):
    accepted_entries = []

    try:
        with open(input_file, 'r', encoding='utf-8') as f:
            lines = f.readlines()
    except FileNotFoundError:
        print(f"Error: File '{input_file}' not found.")
        sys.exit(1)

    for line_num, line in enumerate(lines, 1):
        target = line.strip()
        if not target or target.startswith("#"):
            continue

        # Split host and port (handles IPv6 host formats as well if using square brackets)
        if ":" not in target:
            print(f"[Line {line_num}] Skipping invalid entry (missing port): {target}")
            continue

        host, port = target.rsplit(":", 1)

        # Check if host is an IP address
        if is_valid_ip(host):
            print(f"[+] Accepted IP: {target}")
            accepted_entries.append(target)
            continue

        # Treat host as a domain and check resolution
        print(f"[*] Resolving domain: {host} ...")
        if check_domain_resolves(host, RESOLVERS):
            print(f"[+] Domain resolved successfully: {target}")
            accepted_entries.append(target)
        else:
            print(f"[-] Domain failed resolution across all resolvers: {host}")

    # Write accepted targets to output file
    with open(output_file, 'w', encoding='utf-8') as f:
        for entry in accepted_entries:
            f.write(f"{entry}\n")

    print(f"\nProcessing complete. {len(accepted_entries)} entries saved to '{output_file}'.")

if __name__ == "__main__":
    input_filename = "input.txt"
    output_filename = "output.txt"
    process_targets(input_filename, output_filename)
