# FatxMount

Mounts an Xbox (FATX) or Xbox 360 (XTAF) partition as a read/write drive letter in Windows Explorer,
using [Dokany 2.x](https://github.com/dokan-dev/dokany/releases) + the `DokanNet` NuGet package.

## Build
1. Install the Dokany 2.x driver (matching DokanNet 2.1.0) and the .NET 8 SDK.
2. `dotnet build -c Release`  (or `dotnet publish -c Release -r win-x64 --self-contained false`)

## GUI
Run `FatxMount.exe` with no arguments (it asks for administrator rights, needed for raw disk access).

1. **Source** – pick a physical disk (disks with Xbox partitions are labelled) or "Open image file…".
2. **Partition** – bold rows have a FATX/XTAF signature. "Custom…" lets you type an offset/size.
3. **Mount** – choose a drive letter, tick/untick Read-only, press Mount.

Mounted volumes are listed at the bottom; use *Unmount selected* before unplugging the drive.

## Command line (elevated terminal)
```
FatxMount list 2
FatxMount mount 2 --partition E --drive Z
FatxMount mount 2 --offset 0x130EB0000 --drive Z --readonly
FatxMount create-image test.img 128          # blank scratch image; mount it with --offset 0
```

## Notes
- Back up the drive before the first write. This is new, lightly-tested code.
- Windows may offer to "initialize" the Xbox disk. Decline; if writes are refused, set the disk Offline in Disk Management.
- FATX limits: 42-char ASCII names, 4 GiB max file size, no ACLs/streams/links.

## VirusTotal
- https://www.virustotal.com/gui/file/d5b8d5bc3a3298052c678670b6d5a2526834b91f7b90db51a054b7e51264fe90/details
