package caddynetcp

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"

	"github.com/caddyserver/caddy/v2"
	"github.com/caddyserver/caddy/v2/caddyconfig/caddyfile"
	"github.com/libdns/libdns"
)

const defaultEndpoint = "https://ccp.netcup.net/run/webservice/servers/endpoint.php?JSON"

func init() {
	caddy.RegisterModule(Provider{})
}

// Provider implements Caddy's dns.providers.netcup module with direct Netcup
// CCP API calls. It only implements the libdns methods needed for ACME DNS-01:
// append TXT records and delete TXT records.
type Provider struct {
	CustomerNumber string `json:"customer_number,omitempty"`
	APIKey         string `json:"api_key,omitempty"`
	APIPassword    string `json:"api_password,omitempty"`
	Endpoint       string `json:"endpoint,omitempty"`

	mu sync.Mutex
}

func (Provider) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{
		ID:  "dns.providers.netcup",
		New: func() caddy.Module { return new(Provider) },
	}
}

func (p *Provider) Provision(ctx caddy.Context) error {
	replacer := caddy.NewReplacer()
	p.CustomerNumber = replacer.ReplaceAll(p.CustomerNumber, "")
	p.APIKey = replacer.ReplaceAll(p.APIKey, "")
	p.APIPassword = replacer.ReplaceAll(p.APIPassword, "")
	p.Endpoint = replacer.ReplaceAll(p.Endpoint, "")
	if p.Endpoint == "" {
		p.Endpoint = defaultEndpoint
	}
	return p.Validate()
}

func (p Provider) Validate() error {
	var missing []string
	if p.CustomerNumber == "" {
		missing = append(missing, "customer_number")
	}
	if p.APIKey == "" {
		missing = append(missing, "api_key")
	}
	if p.APIPassword == "" {
		missing = append(missing, "api_password")
	}
	if len(missing) > 0 {
		return fmt.Errorf("missing netcup DNS provider option(s): %s", strings.Join(missing, ", "))
	}
	return nil
}

func (p *Provider) UnmarshalCaddyfile(d *caddyfile.Dispenser) error {
	for d.Next() {
		for d.NextBlock(0) {
			key := d.Val()
			if !d.NextArg() {
				return d.ArgErr()
			}
			value := d.Val()
			switch key {
			case "customer_number":
				p.CustomerNumber = value
			case "api_key":
				p.APIKey = value
			case "api_password":
				p.APIPassword = value
			case "endpoint":
				p.Endpoint = value
			default:
				return d.Errf("unknown netcup DNS provider option %q", key)
			}
			if d.NextArg() {
				return d.ArgErr()
			}
		}
	}
	return nil
}

func (p *Provider) AppendRecords(ctx context.Context, zone string, records []libdns.Record) ([]libdns.Record, error) {
	p.mu.Lock()
	defer p.mu.Unlock()

	sessionID, err := p.login(ctx)
	if err != nil {
		return nil, err
	}
	defer p.logout(ctx, sessionID)

	existing, err := p.infoDNSRecords(ctx, zone, sessionID)
	if err != nil {
		return nil, err
	}

	var toCreate []dnsRecord
	for _, record := range records {
		ncRecord := toNetcupRecord(record)
		if ncRecord.RecType != "TXT" {
			return nil, fmt.Errorf("netcup provider only supports TXT records for ACME, got %s", ncRecord.RecType)
		}
		if !containsExactRecord(existing.DNSRecords, ncRecord) {
			toCreate = append(toCreate, ncRecord)
		}
	}
	if len(toCreate) == 0 {
		return []libdns.Record{}, nil
	}

	if _, err := p.updateDNSRecords(ctx, zone, dnsRecordSet{DNSRecords: toCreate}, sessionID); err != nil {
		return nil, err
	}
	return records, nil
}

func (p *Provider) DeleteRecords(ctx context.Context, zone string, records []libdns.Record) ([]libdns.Record, error) {
	p.mu.Lock()
	defer p.mu.Unlock()

	sessionID, err := p.login(ctx)
	if err != nil {
		return nil, err
	}
	defer p.logout(ctx, sessionID)

	existing, err := p.infoDNSRecords(ctx, zone, sessionID)
	if err != nil {
		return nil, err
	}

	var toDelete []dnsRecord
	for _, record := range records {
		want := toNetcupRecord(record)
		for _, candidate := range existing.DNSRecords {
			if recordMatches(candidate, want) {
				candidate.DeleteRecord = true
				toDelete = append(toDelete, candidate)
			}
		}
	}
	if len(toDelete) == 0 {
		return []libdns.Record{}, nil
	}

	if _, err := p.updateDNSRecords(ctx, zone, dnsRecordSet{DNSRecords: toDelete}, sessionID); err != nil {
		return nil, err
	}
	return records, nil
}

func toNetcupRecord(record libdns.Record) dnsRecord {
	rr := record.RR()
	name := strings.TrimSuffix(rr.Name, ".")
	if name == "" {
		name = "@"
	}
	return dnsRecord{
		HostName:    name,
		RecType:     strings.ToUpper(rr.Type),
		Destination: rr.Data,
	}
}

func containsExactRecord(records []dnsRecord, want dnsRecord) bool {
	for _, record := range records {
		if record.HostName == want.HostName &&
			strings.EqualFold(record.RecType, want.RecType) &&
			record.Destination == want.Destination {
			return true
		}
	}
	return false
}

func recordMatches(candidate, want dnsRecord) bool {
	if candidate.HostName != want.HostName || !strings.EqualFold(candidate.RecType, want.RecType) {
		return false
	}
	return want.Destination == "" || candidate.Destination == want.Destination
}

func cleanZone(zone string) string {
	return strings.TrimSuffix(zone, ".")
}

func (p *Provider) login(ctx context.Context) (string, error) {
	res, err := p.request(ctx, "login", requestParam{
		CustomerNumber: p.CustomerNumber,
		APIKey:         p.APIKey,
		APIPassword:    p.APIPassword,
	})
	if err != nil {
		return "", err
	}
	var data apiSessionData
	if err := json.Unmarshal(res.ResponseData, &data); err != nil {
		return "", err
	}
	if data.APISessionID == "" {
		return "", errors.New("netcup login response did not include an API session ID")
	}
	return data.APISessionID, nil
}

func (p *Provider) logout(ctx context.Context, sessionID string) {
	_, _ = p.request(ctx, "logout", requestParam{
		CustomerNumber: p.CustomerNumber,
		APIKey:         p.APIKey,
		APISessionID:   sessionID,
	})
}

func (p *Provider) infoDNSRecords(ctx context.Context, zone string, sessionID string) (dnsRecordSet, error) {
	res, err := p.request(ctx, "infoDnsRecords", requestParam{
		DomainName:     cleanZone(zone),
		CustomerNumber: p.CustomerNumber,
		APIKey:         p.APIKey,
		APISessionID:   sessionID,
	})
	if err != nil {
		return dnsRecordSet{}, err
	}
	var records dnsRecordSet
	if err := json.Unmarshal(res.ResponseData, &records); err != nil {
		return dnsRecordSet{}, err
	}
	return records, nil
}

func (p *Provider) updateDNSRecords(ctx context.Context, zone string, records dnsRecordSet, sessionID string) (dnsRecordSet, error) {
	res, err := p.request(ctx, "updateDnsRecords", requestParam{
		DomainName:     cleanZone(zone),
		CustomerNumber: p.CustomerNumber,
		APIKey:         p.APIKey,
		APISessionID:   sessionID,
		DNSRecordSet:   records,
	})
	if err != nil {
		return dnsRecordSet{}, err
	}
	var updated dnsRecordSet
	if err := json.Unmarshal(res.ResponseData, &updated); err != nil {
		return dnsRecordSet{}, err
	}
	return updated, nil
}

func (p *Provider) request(ctx context.Context, action string, param requestParam) (response, error) {
	payload, err := json.Marshal(request{Action: action, Param: param})
	if err != nil {
		return response{}, err
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, p.Endpoint, bytes.NewReader(payload))
	if err != nil {
		return response{}, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "caddy-ui-netcup-dns/1.0")

	httpResp, err := http.DefaultClient.Do(req)
	if err != nil {
		return response{}, err
	}
	defer httpResp.Body.Close()

	body, err := io.ReadAll(httpResp.Body)
	if err != nil {
		return response{}, err
	}
	if httpResp.StatusCode < 200 || httpResp.StatusCode >= 300 {
		return response{}, fmt.Errorf("netcup %s HTTP %d: %s", action, httpResp.StatusCode, string(body))
	}

	var res response
	if err := json.Unmarshal(body, &res); err != nil {
		return response{}, err
	}
	if res.Status != "success" {
		message := strings.TrimSpace(res.LongMessage)
		if message == "" {
			message = strings.TrimSpace(res.ShortMessage)
		}
		if message == "" {
			message = string(body)
		}
		return response{}, fmt.Errorf("netcup %s failed: %s", action, message)
	}
	return res, nil
}

var (
	_ caddy.Module          = (*Provider)(nil)
	_ caddy.Provisioner     = (*Provider)(nil)
	_ caddy.Validator       = (*Provider)(nil)
	_ caddyfile.Unmarshaler = (*Provider)(nil)
	_ libdns.RecordAppender = (*Provider)(nil)
	_ libdns.RecordDeleter  = (*Provider)(nil)
)

