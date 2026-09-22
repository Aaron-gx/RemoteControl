package main

import (
	"crypto/ecdsa"
	"crypto/x509"
	"encoding/pem"
	"fmt"
	"os"
	"testing"
	"time"
)

// 该测试需要厂商私钥（不在仓库里）：存在则用真实私钥签一条激活码，
// 写文件供 C# 侧验签（验证 Go ↔ C# 的签名格式互操作）；不存在则跳过。
func TestInteropSignWithVendorKey(t *testing.T) {
	const keyPath = `E:\RemoteControl\license\private.pem`
	b, err := os.ReadFile(keyPath)
	if err != nil {
		t.Skipf("没有厂商私钥（%s），跳过互操作测试", keyPath)
	}
	blk, _ := pem.Decode(b)
	if blk == nil {
		t.Fatal("私钥不是合法 PEM")
	}
	privAny, err := x509.ParsePKCS8PrivateKey(blk.Bytes)
	if err != nil {
		t.Fatal(err)
	}
	priv, ok := privAny.(*ecdsa.PrivateKey)
	if !ok {
		t.Fatal("不是 ECDSA 私钥")
	}
	now := time.Now()
	code := mustSign(t, priv, LicensePayload{
		Lic: "INTEROP-1", Sub: "跨语言互操作", Srv: "DEP-INTEROP",
		Nbf: now.Add(-time.Minute).Unix(), Exp: now.Add(24 * time.Hour).Unix(),
	})
	// 用内置公钥自验一次（Go 侧闭环）
	if _, err := VerifyLicense(code, "DEP-INTEROP", "", now); err != nil {
		t.Fatalf("Go 侧自验失败：%v", err)
	}
	out := `E:\tools\interop-code.txt`
	_ = os.WriteFile(out, []byte(code), 0o644)
	fmt.Printf("interop code -> %s\n%s\n", out, code)
}
