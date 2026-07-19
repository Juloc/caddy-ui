package caddynetcp

import "testing"

func TestCleanZone(t *testing.T) {
	if got := cleanZone("example.com."); got != "example.com" {
		t.Fatalf("cleanZone() = %q", got)
	}
}

func TestTXTRecordComparisonIgnoresQuotes(t *testing.T) {
	records := []dnsRecord{{HostName: "_acme-challenge", RecType: "TXT", Destination: `"token"`}}
	want := dnsRecord{HostName: "_acme-challenge", RecType: "txt", Destination: "token"}
	if !containsExactRecord(records, want) {
		t.Fatal("expected quoted and unquoted TXT values to match")
	}
}
