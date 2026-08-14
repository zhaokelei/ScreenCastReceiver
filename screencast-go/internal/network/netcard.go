//go:build windows
// +build windows

package network

import (
	"fmt"
	"net"
	"sort"
	"strings"

	"screencast-go/internal/logger"
	"screencast-go/internal/models"
)

// physicalKeywords 物理网卡描述的关键词（命中其一判定为物理，否则虚拟）
var physicalKeywords = []string{
	// 主流有线/无线芯片厂商
	"Realtek PCIe", "Realtek USB", "Realtek RTL88", "Realtek Gaming",
	"Intel(R) Ethernet", "Intel(R) Wi-Fi", "Intel(R) Wireless",
	"Intel(R) PRO/1000", "Intel I225", "Intel I226",
	"Killer(R)", "Qualcomm Atheros", "Broadcom NetXtreme", "Broadcom BCM",
	"MediaTek Wi-Fi", "MediaTek MT7921", "MediaTek Ethernet",
	"Marvell Yukon", "Marvell AVASTAR", "Aquantia AQtion",
	// USB 外置网卡
	"ASIX AX88179", "ASIX AX88178", "Plugable", "StarTech",
	// 一般关键词
	"Ethernet Controller", "Wi-Fi", "Wireless-AC", "Wireless-AX",
	"802.11", "Gigabit Ethernet", "Gigabit Network Connection",
}

// virtualKeywords 虚拟网卡关键词（优先判定）
var virtualKeywords = []string{
	// 常见虚拟
	"Hyper-V", "Virtual Switch", "Virtual Adapter", "Miniport",
	"VMware", "VirtualBox", "TAP-Windows", "TAP-ProtonVPN",
	"WSL", "OpenVPN", "WireGuard", "Tailscale", "ZeroTier",
	"Loopback", "WAN Miniport", "Bluetooth Network",
	"Microsoft Wi-Fi Direct", "Microsoft Hosted Network",
	"vEthernet", "Virtual Machine",
}

// EnumNICs 枚举所有网卡，区分物理/虚拟 + 提取IPv4/IPv6
func EnumNICs(log *logger.RingLogger) ([]models.NICInfo, error) {
	if log == nil { log = logger.Default }
	ifaces, err := net.Interfaces()
	if err != nil {
		log.Error("网络", "枚举网卡失败: %v", err)
		return nil, err
	}

	list := make([]models.NICInfo, 0, len(ifaces))
	for _, itf := range ifaces {
		// Go标准库net.Interface没有Description（只Windows有）；这里先用Name代替（"以太网"/"Wi-Fi"这种）
		// 后续版本可用 windows.GetAdaptersAddresses 取完整Description
		desc := itf.Name
		n := models.NICInfo{
			Index:       itf.Index,
			Name:        itf.Name,
			Description: desc,
			MAC:         itf.HardwareAddr.String(),
			MTU:         itf.MTU,
			OperUp:      (itf.Flags & net.FlagUp) != 0,
		}
		// 判定虚拟/物理：先看Name是否命中虚拟关键词（因为标准库无Description）
		lDesc := strings.ToLower(desc)
		lName := strings.ToLower(n.Name)
		for _, kw := range virtualKeywords {
			if strings.Contains(lDesc, strings.ToLower(kw)) ||
				strings.Contains(lName, strings.ToLower(kw)) {
				n.IsPhysical = false
				goto skip
			}
		}
		for _, kw := range physicalKeywords {
			if strings.Contains(lDesc, strings.ToLower(kw)) ||
				strings.Contains(lName, strings.ToLower(kw)) {
				n.IsPhysical = true
				goto skip
			}
		}
		// 兜底：Loopback=虚拟，带MAC且Up=猜测物理
		if itf.Flags & net.FlagLoopback != 0 {
			n.IsPhysical = false
		} else if n.MAC != "" && n.OperUp && n.MTU > 576 {
			n.IsPhysical = true
		} else {
			n.IsPhysical = false
		}
	skip:
		// 提取绑定IP
		addrs, err := itf.Addrs()
		if err != nil { continue }
		for _, a := range addrs {
			var ip net.IP
			switch v := a.(type) {
			case *net.IPNet: ip = v.IP
			case *net.IPAddr: ip = v.IP
			}
			if ip == nil { continue }
			if ip4 := ip.To4(); ip4 != nil {
				// 忽略 127.x / 169.254 链接本地
				if !ip4.IsLoopback() && !ip4.IsLinkLocalUnicast() {
					n.IPv4List = append(n.IPv4List, ip4.String())
				}
			} else if ip.To16() != nil {
				if !ip.IsLoopback() && !ip.IsLinkLocalUnicast() && !ip.IsMulticast() {
					n.IPv6List = append(n.IPv6List, ip.String())
				}
			}
		}
		list = append(list, n)
	}
	sort.SliceStable(list, func(i, j int) bool {
		// 先排物理再排虚拟，组内按名称
		if list[i].IsPhysical != list[j].IsPhysical {
			return list[i].IsPhysical
		}
		return list[i].Index < list[j].Index
	})
	return list, nil
}

// GetBindAddresses 根据配置计算实际要绑定的所有IPv4地址
// bindIndexes: 选中的网卡Index集合，空=全部物理
// onlyIPv4: 是否只绑定IPv4
// customIPs: 手动补充IP
func GetBindAddresses(cfg *models.AppConfig, log *logger.RingLogger) ([]string, error) {
	if log == nil { log = logger.Default }
	ifaces, err := EnumNICs(log)
	if err != nil { return nil, err }

	physicalCnt := 0
	virtualCnt := 0
	out := make([]string, 0, 8)
	seen := map[string]struct{}{}
	add := func(ip string) {
		if ip == "" { return }
		if _, ok := seen[ip]; ok { return }
		seen[ip] = struct{}{}
		out = append(out, ip)
	}

	bindSet := map[int]struct{}{}
	for _, i := range cfg.BindNICIndex { bindSet[i] = struct{}{} }
	bindFromConfig := len(bindSet) > 0

	for _, itf := range ifaces {
		if itf.IsPhysical { physicalCnt++ } else { virtualCnt++ }
		var shouldUse bool
		if !itf.OperUp || len(itf.IPv4List) == 0 {
			continue // Down/无IPv4=跳过
		}
		if bindFromConfig {
			_, ok := bindSet[itf.Index]
			shouldUse = ok
		} else {
			// 配置为空=默认勾全部物理网卡（兜底策略）
			shouldUse = itf.IsPhysical
		}
		if !shouldUse { continue }
		for _, ip := range itf.IPv4List { add(ip) }
		if !cfg.BindOnlyIPv4 {
			for _, ip := range itf.IPv6List { add(ip) }
		}
	}
	for _, ip := range cfg.BindCustomIPs { add(ip) }
	log.Info("网络", "发现物理网卡 %d 张、虚拟网卡 %d 张", physicalCnt, virtualCnt)
	log.Info("网络", "绑定网卡更新: %d 个 IPv4 地址 (%s)", len(out), strings.Join(out, ", "))
	return out, nil
}

// GetOutboundIP 首选对外IPv4（用于HTTP server 0.0.0.0绑定时显示给用户的可访问IP）
func GetOutboundIP(cfg *models.AppConfig, log *logger.RingLogger) string {
	ips, err := GetBindAddresses(cfg, log)
	if err != nil || len(ips) == 0 {
		// 兜底：拿默认路由出口
		conn, err2 := net.Dial("udp", "8.8.8.8:80")
		if err2 != nil { return "127.0.0.1" }
		defer conn.Close()
		udp := conn.LocalAddr().(*net.UDPAddr)
		if udp.IP.To4() != nil { return udp.IP.String() }
		return "127.0.0.1"
	}
	return ips[0]
}

// StatsSample 瞬时网络流量样本（用于计算KB/s）
type StatsSample struct {
	RxBytes uint64
	TxBytes uint64
	At      int64 // unix毫秒
}

// DiffKBs 两个样本之间的平均速率（KB/s）
func (a StatsSample) DiffKBs(b StatsSample) (rx, tx float64) {
	dtMs := b.At - a.At
	if dtMs <= 0 { return 0, 0 }
	dt := float64(dtMs) / 1000.0
	if b.RxBytes >= a.RxBytes { rx = float64(b.RxBytes-a.RxBytes) / 1024.0 / dt }
	if b.TxBytes >= a.TxBytes { tx = float64(b.TxBytes-a.TxBytes) / 1024.0 / dt }
	return
}

// 占位：避免 fmt 报未使用
var _ = fmt.Sprintf
