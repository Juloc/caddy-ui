package main

import (
	caddycmd "github.com/caddyserver/caddy/v2/cmd"

	_ "github.com/caddyserver/caddy/v2/modules/standard"
	_ "github.com/juloc/caddy-ui/caddynetcp"
)

func main() {
	caddycmd.Main()
}
