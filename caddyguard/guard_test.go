package caddyguard

import (
	"net/http/httptest"
	"testing"
	"time"
)

func TestGuardRateLimitAndTemporaryRestriction(t *testing.T) {
	guard := &Guard{Requests: 2, Window: "1m", Burst: 0, Block: "15m"}
	if err := guard.initialize(); err != nil {
		t.Fatalf("initialize: %v", err)
	}
	if err := guard.Validate(); err != nil {
		t.Fatalf("validate: %v", err)
	}

	if allowed, _, _ := guard.rateAllowed("203.0.113.10"); !allowed {
		t.Fatal("first request should be allowed")
	}
	if allowed, _, _ := guard.rateAllowed("203.0.113.10"); !allowed {
		t.Fatal("second request should be allowed")
	}
	for index := 0; index < 2; index++ {
		if allowed, _, _ := guard.rateAllowed("203.0.113.10"); allowed {
			t.Fatal("request above the token bucket should be limited")
		}
	}
	allowed, retry, reason := guard.rateAllowed("203.0.113.10")
	if allowed {
		t.Fatal("repeated violations should create a temporary restriction")
	}
	if retry < 14*time.Minute {
		t.Fatalf("expected temporary restriction, got retry %v", retry)
	}
	if reason == "" {
		t.Fatal("restriction should include a reason")
	}
}

func TestGuardDoesNotTrustForwardedHeadersByDefault(t *testing.T) {
	guard := &Guard{Requests: 10, Window: "1m", Block: "15m"}
	if err := guard.initialize(); err != nil {
		t.Fatalf("initialize: %v", err)
	}
	request := httptest.NewRequest("GET", "https://example.com/", nil)
	request.RemoteAddr = "10.1.2.3:4567"
	request.Header.Set("X-Forwarded-For", "203.0.113.99")
	if got := guard.clientIP(request); got != "10.1.2.3" {
		t.Fatalf("untrusted proxy header used: got %q", got)
	}
}

func TestGuardUsesForwardedClientOnlyFromConfiguredProxy(t *testing.T) {
	guard := &Guard{Requests: 10, Window: "1m", Block: "15m", TrustedProxy: []string{"10.1.2.3/32"}}
	if err := guard.initialize(); err != nil {
		t.Fatalf("initialize: %v", err)
	}
	request := httptest.NewRequest("GET", "https://example.com/", nil)
	request.RemoteAddr = "10.1.2.3:4567"
	request.Header.Set("X-Forwarded-For", "203.0.113.99")
	if got := guard.clientIP(request); got != "203.0.113.99" {
		t.Fatalf("trusted proxy client not resolved: got %q", got)
	}
}
