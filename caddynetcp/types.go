package caddynetcp

import "encoding/json"

type dnsRecord struct {
	ID           string `json:"id,omitempty"`
	HostName     string `json:"hostname"`
	RecType      string `json:"type"`
	Priority     int    `json:"priority,string,omitempty"`
	Destination  string `json:"destination"`
	DeleteRecord bool   `json:"deleterecord,omitempty"`
}

type dnsRecordSet struct {
	DNSRecords []dnsRecord `json:"dnsrecords"`
}

type apiSessionData struct {
	APISessionID string `json:"apisessionid"`
}

type requestParam struct {
	DomainName     string       `json:"domainname,omitempty"`
	CustomerNumber string       `json:"customernumber"`
	APIKey         string       `json:"apikey"`
	APIPassword    string       `json:"apipassword,omitempty"`
	APISessionID   string       `json:"apisessionid,omitempty"`
	DNSRecordSet   dnsRecordSet `json:"dnsrecordset,omitempty"`
}

type request struct {
	Action string       `json:"action"`
	Param  requestParam `json:"param"`
}

type response struct {
	Action       string          `json:"action"`
	Status       string          `json:"status"`
	ShortMessage string          `json:"shortmessage"`
	LongMessage  string          `json:"longmessage"`
	ResponseData json.RawMessage `json:"responsedata"`
}
