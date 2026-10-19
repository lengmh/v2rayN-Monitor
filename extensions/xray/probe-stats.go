// Package probestats counts returned HTTP probe streams, without changing native outbound counters.
package probestats

import (
	"context"
	"net"
	"strings"

	"github.com/xtls/xray-core/app/proxyman"
	"github.com/xtls/xray-core/core"
	"github.com/xtls/xray-core/features/outbound"
	"github.com/xtls/xray-core/features/policy"
	"github.com/xtls/xray-core/features/stats"
)

const Prefix = "monitor>>>probe>>>observatory>>>"

type Pair struct {
	Uplink   int64 `json:"uplink"`
	Downlink int64 `json:"downlink"`
}

type Snapshot struct {
	Version int             `json:"version"`
	Kind    string          `json:"kind"`
	Members map[string]Pair `json:"members"`
	Totals  map[string]Pair `json:"totals"`
}

func managers(ctx context.Context) (stats.Manager, outbound.Manager) {
	instance := core.FromContext(ctx)
	if instance == nil {
		return nil, nil
	}
	manager, _ := instance.GetFeature(stats.ManagerType()).(stats.Manager)
	handlers, _ := instance.GetFeature(outbound.ManagerType()).(outbound.Manager)
	policies, _ := instance.GetFeature(policy.ManagerType()).(policy.Manager)
	if manager == nil || handlers == nil || policies == nil ||
		!policies.ForSystem().Stats.OutboundUplink || !policies.ForSystem().Stats.OutboundDownlink {
		return nil, nil
	}
	return manager, handlers
}

func category(handler outbound.Handler) string {
	if handler == nil {
		return "unknown"
	}
	if sender := handler.SenderSettings(); sender != nil {
		value, err := sender.GetInstance()
		settings, ok := value.(*proxyman.SenderConfig)
		if err != nil || !ok || settings.ProxySettings.HasTag() ||
			len(settings.GetStreamSettings().GetSocketSettings().GetDialerProxy()) != 0 {
			return "unknown"
		}
	}
	settings := handler.ProxySettings()
	if settings == nil {
		return "unknown"
	}
	switch settings.Type {
	case "xray.proxy.freedom.Config", "xray.proxy.blackhole.Config":
		return "excluded"
	case "xray.proxy.socks.ClientConfig", "xray.proxy.http.ClientConfig",
		"xray.proxy.shadowsocks.ClientConfig", "xray.proxy.shadowsocks_2022.ClientConfig",
		"xray.proxy.vmess.outbound.Config",
		"xray.proxy.vless.outbound.Config", "xray.proxy.trojan.ClientConfig",
		"xray.proxy.hysteria.ClientConfig", "xray.proxy.tuic.ClientConfig":
		return "business"
	default:
		return "unknown"
	}
}

func counters(manager stats.Manager, key string) (stats.Counter, stats.Counter) {
	up, _ := manager.GetOrRegisterCounter(Prefix + key + ">>>traffic>>>uplink")
	down, _ := manager.GetOrRegisterCounter(Prefix + key + ">>>traffic>>>downlink")
	return up, down
}

// Read initializes zero counters only for the actual handler directory. It never dials or resets.
func Read(ctx context.Context) *Snapshot {
	manager, handlers := managers(ctx)
	if manager == nil {
		return nil
	}
	result := &Snapshot{Version: 1, Kind: "observatory", Members: map[string]Pair{}, Totals: map[string]Pair{}}
	for _, handler := range handlers.ListHandlers(ctx) {
		tag := handler.Tag()
		if tag == "" || strings.Contains(tag, ">>>") {
			continue
		}
		up, down := counters(manager, "member>>>"+tag)
		if up != nil && down != nil {
			result.Members[tag] = Pair{up.Value(), down.Value()}
		}
	}
	for _, basis := range []string{"business", "excluded", "unknown"} {
		up, down := counters(manager, basis)
		if up != nil && down != nil {
			result.Totals[basis] = Pair{up.Value(), down.Value()}
		}
	}
	return result
}

// Wrap is called after the forced-tag dial returns. Each connection retains its own tag and counters.
func Wrap(ctx context.Context, conn net.Conn, tag string) net.Conn {
	manager, handlers := managers(ctx)
	if manager == nil || conn == nil {
		return conn
	}
	basis := category(handlers.GetHandler(tag))
	up, down := counters(manager, basis)
	var memberUp, memberDown stats.Counter
	if tag != "" && !strings.Contains(tag, ">>>") {
		memberUp, memberDown = counters(manager, "member>>>"+tag)
	}
	if up == nil || down == nil {
		return conn
	}
	return &connection{Conn: conn, up: up, down: down, memberUp: memberUp, memberDown: memberDown}
}

type connection struct {
	net.Conn
	up, down, memberUp, memberDown stats.Counter
}

func (c *connection) Read(buffer []byte) (int, error) {
	n, err := c.Conn.Read(buffer)
	if n > 0 {
		c.down.Add(int64(n))
		if c.memberDown != nil {
			c.memberDown.Add(int64(n))
		}
	}
	return n, err
}

func (c *connection) Write(buffer []byte) (int, error) {
	n, err := c.Conn.Write(buffer)
	if n > 0 {
		c.up.Add(int64(n))
		if c.memberUp != nil {
			c.memberUp.Add(int64(n))
		}
	}
	return n, err
}
