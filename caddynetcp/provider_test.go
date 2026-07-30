package caddynetcp

import (
	"os"
	"path/filepath"
	"testing"
)

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

func TestReadRuntimeSecretFromEnvironmentPlaceholderFile(t *testing.T) {
	directory := t.TempDir()
	path := filepath.Join(directory, "CADDY_UI_PROVIDER_TEST_API_KEY")
	if err := os.WriteFile(path, []byte("private-value\n"), 0o600); err != nil {
		t.Fatal(err)
	}

	value, err := readRuntimeSecret("{env.CADDY_UI_PROVIDER_TEST_API_KEY}", directory)
	if err != nil {
		t.Fatal(err)
	}
	if value != "private-value" {
		t.Fatalf("readRuntimeSecret() = %q", value)
	}
}

func TestEnvironmentPlaceholderRejectsUnsafeName(t *testing.T) {
	if _, ok := environmentPlaceholderName("{env.BAD/NAME}"); ok {
		t.Fatal("unsafe environment placeholder was accepted")
	}
}
