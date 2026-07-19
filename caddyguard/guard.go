package caddyguard

import (
	"bufio"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/caddyserver/caddy/v2"
	"github.com/caddyserver/caddy/v2/caddyconfig/caddyfile"
	"github.com/caddyserver/caddy/v2/caddyconfig/httpcaddyfile"
	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
)

func init() {
	caddy.RegisterModule(Guard{})
	httpcaddyfile.RegisterHandlerDirective("caddy_ui_guard", parseCaddyfile)
	httpcaddyfile.RegisterDirectiveOrder("caddy_ui_guard", httpcaddyfile.Before, "redir")
}

// Guard is a lightweight per-client request limiter used by Caddy UI managed routes.
// It also consumes a dynamic blocklist file written atomically by the Caddy UI companion.
type Guard struct {
	Requests      int      `json:"requests,omitempty"`
	Window        string   `json:"window,omitempty"`
	Burst         int      `json:"burst,omitempty"`
	Block         string   `json:"block,omitempty"`
	BlocklistFile string   `json:"blocklist_file,omitempty"`
	EventLog      string   `json:"event_log,omitempty"`
	TrustedProxy  []string `json:"trusted_proxy,omitempty"`
	Allowlist     []string `json:"allowlist,omitempty"`

	mu              sync.Mutex
	windowDuration  time.Duration
	blockDuration   time.Duration
	clients         map[string]*clientState
	trustedNetworks []*net.IPNet
	allowedNetworks []*net.IPNet
	fileBans        map[string]fileBan
	blocklistMTime  time.Time
	lastBlockCheck  time.Time
	eventCooldown   map[string]time.Time
}

type clientState struct {
	Tokens       float64
	UpdatedAt    time.Time
	BlockedUntil time.Time
	Violations   int
	LastSeen     time.Time
}

type fileBan struct {
	ExpiresAt time.Time
	Reason    string
}

// CaddyModule returns the Caddy module information.
func (Guard) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{
		ID:  "http.handlers.caddy_ui_guard",
		New: func() caddy.Module { return new(Guard) },
	}
}

// Provision prepares runtime-only state.
func (g *Guard) Provision(ctx caddy.Context) error {
	return g.initialize()
}

func (g *Guard) initialize() error {
	if g.Requests <= 0 {
		g.Requests = 300
	}
	if g.Window == "" {
		g.Window = "1m"
	}
	if g.Block == "" {
		g.Block = "15m"
	}
	window, err := time.ParseDuration(g.Window)
	if err != nil || window <= 0 {
		return fmt.Errorf("invalid caddy_ui_guard window %q", g.Window)
	}
	block, err := time.ParseDuration(g.Block)
	if err != nil || block <= 0 {
		return fmt.Errorf("invalid caddy_ui_guard block duration %q", g.Block)
	}
	if g.Burst < 0 {
		return fmt.Errorf("caddy_ui_guard burst cannot be negative")
	}
	trusted, err := parseNetworks(g.TrustedProxy)
	if err != nil {
		return fmt.Errorf("trusted proxy: %w", err)
	}
	allowed, err := parseNetworks(g.Allowlist)
	if err != nil {
		return fmt.Errorf("allowlist: %w", err)
	}
	g.windowDuration = window
	g.blockDuration = block
	g.trustedNetworks = trusted
	g.allowedNetworks = allowed
	g.clients = make(map[string]*clientState)
	g.fileBans = make(map[string]fileBan)
	g.eventCooldown = make(map[string]time.Time)
	return nil
}

// Validate checks the configured limits.
func (g *Guard) Validate() error {
	if g.Requests <= 0 {
		return fmt.Errorf("caddy_ui_guard requests must be greater than zero")
	}
	if g.windowDuration <= 0 || g.blockDuration <= 0 {
		return fmt.Errorf("caddy_ui_guard durations are not initialized")
	}
	return nil
}

// ServeHTTP enforces dynamic bans and a token-bucket limiter before continuing the handler chain.
func (g *Guard) ServeHTTP(w http.ResponseWriter, r *http.Request, next caddyhttp.Handler) error {
	clientIP := g.clientIP(r)
	if clientIP == "" || g.isAllowlisted(clientIP) {
		return next.ServeHTTP(w, r)
	}

	if banned, retry, reason := g.fileBlocked(clientIP); banned {
		g.emitLimited("blocked", "warning", clientIP, r, reason, retry)
		writeBlocked(w, http.StatusForbidden, retry)
		return nil
	}

	allowed, retry, reason := g.rateAllowed(clientIP)
	if !allowed {
		kind := "rate_limit"
		severity := "info"
		if strings.HasPrefix(reason, "temporarily blocked") {
			kind = "blocked"
			severity = "warning"
		}
		g.emitLimited(kind, severity, clientIP, r, reason, retry)
		writeBlocked(w, http.StatusTooManyRequests, retry)
		return nil
	}
	return next.ServeHTTP(w, r)
}

func writeBlocked(w http.ResponseWriter, status int, retry time.Duration) {
	seconds := int(retry.Round(time.Second).Seconds())
	if seconds < 1 {
		seconds = 1
	}
	w.Header().Set("Retry-After", strconv.Itoa(seconds))
	w.Header().Set("Cache-Control", "no-store")
	http.Error(w, http.StatusText(status), status)
}

func (g *Guard) rateAllowed(clientIP string) (bool, time.Duration, string) {
	now := time.Now()
	g.mu.Lock()
	defer g.mu.Unlock()

	state := g.clients[clientIP]
	capacity := float64(g.Requests + g.Burst)
	if state == nil {
		state = &clientState{Tokens: capacity, UpdatedAt: now, LastSeen: now}
		g.clients[clientIP] = state
	}
	if state.BlockedUntil.After(now) {
		return false, time.Until(state.BlockedUntil), "temporarily blocked after repeated rate-limit violations"
	}

	elapsed := now.Sub(state.UpdatedAt).Seconds()
	refillPerSecond := float64(g.Requests) / g.windowDuration.Seconds()
	state.Tokens += elapsed * refillPerSecond
	if state.Tokens > capacity {
		state.Tokens = capacity
	}
	state.UpdatedAt = now
	state.LastSeen = now

	if state.Tokens >= 1 {
		state.Tokens--
		if state.Violations > 0 && elapsed >= g.windowDuration.Seconds() {
			state.Violations--
		}
		g.pruneClientsLocked(now)
		return true, 0, ""
	}

	state.Violations++
	retry := time.Duration(float64(time.Second) / refillPerSecond)
	if retry < time.Second {
		retry = time.Second
	}
	if state.Violations >= 3 {
		block := g.blockDuration
		if state.Violations >= 10 && block < 24*time.Hour {
			block = 24 * time.Hour
		} else if state.Violations >= 6 && block < time.Hour {
			block = time.Hour
		}
		state.BlockedUntil = now.Add(block)
		return false, block, "temporarily blocked after repeated rate-limit violations"
	}
	return false, retry, "request rate exceeded"
}

func (g *Guard) pruneClientsLocked(now time.Time) {
	if len(g.clients) < 10_000 {
		return
	}
	cutoff := now.Add(-2 * g.windowDuration)
	for key, value := range g.clients {
		if value.LastSeen.Before(cutoff) && value.BlockedUntil.Before(now) {
			delete(g.clients, key)
		}
	}
}

func (g *Guard) fileBlocked(clientIP string) (bool, time.Duration, string) {
	g.refreshBlocklist()
	g.mu.Lock()
	defer g.mu.Unlock()
	ban, ok := g.fileBans[clientIP]
	if !ok {
		return false, 0, ""
	}
	if !ban.ExpiresAt.After(time.Now()) {
		delete(g.fileBans, clientIP)
		return false, 0, ""
	}
	return true, time.Until(ban.ExpiresAt), ban.Reason
}

func (g *Guard) refreshBlocklist() {
	if g.BlocklistFile == "" {
		return
	}
	now := time.Now()
	g.mu.Lock()
	if now.Sub(g.lastBlockCheck) < 2*time.Second {
		g.mu.Unlock()
		return
	}
	g.lastBlockCheck = now
	g.mu.Unlock()

	info, err := os.Stat(g.BlocklistFile)
	if err != nil {
		if os.IsNotExist(err) {
			g.mu.Lock()
			g.fileBans = make(map[string]fileBan)
			g.blocklistMTime = time.Time{}
			g.mu.Unlock()
		}
		return
	}
	g.mu.Lock()
	if info.ModTime().Equal(g.blocklistMTime) {
		g.mu.Unlock()
		return
	}
	g.mu.Unlock()

	file, err := os.Open(g.BlocklistFile)
	if err != nil {
		return
	}
	defer file.Close()
	bans := make(map[string]fileBan)
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		parts := strings.SplitN(scanner.Text(), "|", 3)
		if len(parts) < 2 || net.ParseIP(strings.TrimSpace(parts[0])) == nil {
			continue
		}
		expires, err := time.Parse(time.RFC3339Nano, strings.TrimSpace(parts[1]))
		if err != nil || !expires.After(now) {
			continue
		}
		reason := "blocked by Caddy UI security policy"
		if len(parts) == 3 && strings.TrimSpace(parts[2]) != "" {
			reason = strings.TrimSpace(parts[2])
		}
		bans[strings.TrimSpace(parts[0])] = fileBan{ExpiresAt: expires, Reason: reason}
	}
	g.mu.Lock()
	g.fileBans = bans
	g.blocklistMTime = info.ModTime()
	g.mu.Unlock()
}

func (g *Guard) clientIP(r *http.Request) string {
	peer := r.RemoteAddr
	if host, _, err := net.SplitHostPort(peer); err == nil {
		peer = host
	}
	peerIP := net.ParseIP(peer)
	if peerIP == nil || !ipInNetworks(peerIP, g.trustedNetworks) {
		return peer
	}

	forwarded := strings.Split(r.Header.Get("X-Forwarded-For"), ",")
	for index := len(forwarded) - 1; index >= 0; index-- {
		candidate := strings.TrimSpace(forwarded[index])
		ip := net.ParseIP(candidate)
		if ip == nil {
			continue
		}
		if !ipInNetworks(ip, g.trustedNetworks) {
			return ip.String()
		}
	}
	if realIP := net.ParseIP(strings.TrimSpace(r.Header.Get("X-Real-IP"))); realIP != nil {
		return realIP.String()
	}
	return peer
}

func (g *Guard) isAllowlisted(clientIP string) bool {
	ip := net.ParseIP(clientIP)
	return ip != nil && ipInNetworks(ip, g.allowedNetworks)
}

func parseNetworks(values []string) ([]*net.IPNet, error) {
	result := make([]*net.IPNet, 0, len(values))
	for _, value := range values {
		value = strings.TrimSpace(value)
		if value == "" {
			continue
		}
		if !strings.Contains(value, "/") {
			ip := net.ParseIP(value)
			if ip == nil {
				return nil, fmt.Errorf("invalid IP or network %q", value)
			}
			bits := 128
			if ip.To4() != nil {
				bits = 32
			}
			value = fmt.Sprintf("%s/%d", value, bits)
		}
		_, network, err := net.ParseCIDR(value)
		if err != nil {
			return nil, fmt.Errorf("invalid network %q", value)
		}
		result = append(result, network)
	}
	return result, nil
}

func ipInNetworks(ip net.IP, networks []*net.IPNet) bool {
	for _, network := range networks {
		if network.Contains(ip) {
			return true
		}
	}
	return false
}

func (g *Guard) emitLimited(kind, severity, clientIP string, r *http.Request, reason string, retry time.Duration) {
	if g.EventLog == "" {
		return
	}
	key := kind + ":" + clientIP
	now := time.Now()
	g.mu.Lock()
	last := g.eventCooldown[key]
	if now.Sub(last) < 30*time.Second {
		g.mu.Unlock()
		return
	}
	g.eventCooldown[key] = now
	g.mu.Unlock()

	entry := map[string]any{
		"ts":          now.UTC().Format(time.RFC3339Nano),
		"kind":        kind,
		"severity":    severity,
		"client_ip":   clientIP,
		"host":        r.Host,
		"endpoint":    r.URL.Path,
		"reason":      reason,
		"retry_after": int(retry.Round(time.Second).Seconds()),
	}
	payload, err := json.Marshal(entry)
	if err != nil {
		return
	}
	file, err := os.OpenFile(g.EventLog, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		return
	}
	defer file.Close()
	_, _ = file.Write(append(payload, '\n'))
}

// UnmarshalCaddyfile implements caddyfile.Unmarshaler.
//
// caddy_ui_guard {
//     requests 300
//     window 1m
//     burst 60
//     block 15m
//     blocklist_file /etc/caddy/security-blocklist.txt
//     event_log /var/log/caddy/security.log
//     trusted_proxy 10.0.0.0/8
//     allowlist 192.168.0.0/16
// }
func (g *Guard) UnmarshalCaddyfile(d *caddyfile.Dispenser) error {
	d.Next()
	for nesting := d.Nesting(); d.NextBlock(nesting); {
		key := d.Val()
		if !d.NextArg() {
			return d.ArgErr()
		}
		value := d.Val()
		switch key {
		case "requests":
			parsed, err := strconv.Atoi(value)
			if err != nil || parsed <= 0 {
				return d.Errf("requests must be a positive integer")
			}
			g.Requests = parsed
		case "window":
			g.Window = value
		case "burst":
			parsed, err := strconv.Atoi(value)
			if err != nil || parsed < 0 {
				return d.Errf("burst must be a non-negative integer")
			}
			g.Burst = parsed
		case "block":
			g.Block = value
		case "blocklist_file":
			g.BlocklistFile = value
		case "event_log":
			g.EventLog = value
		case "trusted_proxy":
			g.TrustedProxy = append(g.TrustedProxy, value)
		case "allowlist":
			g.Allowlist = append(g.Allowlist, value)
		default:
			return d.Errf("unknown caddy_ui_guard option %q", key)
		}
		if d.NextArg() {
			return d.ArgErr()
		}
	}
	return nil
}

func parseCaddyfile(h httpcaddyfile.Helper) (caddyhttp.MiddlewareHandler, error) {
	var guard Guard
	if err := guard.UnmarshalCaddyfile(h.Dispenser); err != nil {
		return nil, err
	}
	return &guard, nil
}

var (
	_ caddy.Module                = (*Guard)(nil)
	_ caddy.Provisioner           = (*Guard)(nil)
	_ caddy.Validator             = (*Guard)(nil)
	_ caddyhttp.MiddlewareHandler = (*Guard)(nil)
	_ caddyfile.Unmarshaler       = (*Guard)(nil)
)
