# FatxMount 1.0

Mounts an original Xbox (FATX) or Xbox 360 (XTAF) partition as a read/write drive letter in Windows Explorer,
using [Dokany 2.x](https://github.com/dokan-dev/dokany/releases) and the `DokanNet` package. It can also format
Xbox partitions and keeps a write-ahead journal so a crash or power cut doesn't corrupt the drive.

## Requirements
- Windows 10/11, **Dokany 2.x driver** installed (needed to run, not to build).
- To build: Visual Studio 2022 17.8+ with the ".NET desktop development" workload (or the .NET 8 SDK).

## Build
- Visual Studio: open `FatxMount.csproj`, set the configuration to **Release**, Build → Build Solution.
  Output: `bin\Release\net8.0-windows\FatxMount.exe`.
- Or run `build-release.cmd` for a single self-contained `publish\FatxMount.exe` (no .NET install needed on the target PC).

## Using the GUI
Run `FatxMount.exe` (it asks for administrator rights, which raw disk access needs).

1. **Source** – pick a physical disk or **Open image…**. Disks containing Xbox partitions are labelled, and a disk with an
   Xbox 360 Data partition is selected first.
2. **Partition** – bold rows have a FATX/XTAF signature. The **Data360** partition is auto-selected when present
   (otherwise the first detected partition). Click any other row to override, untick the auto-detect box to turn it off,
   or choose **Custom…** and type an offset/size.
3. **Mount** – pick a drive letter, untick **Read-only** for read/write, leave **Journaling** on, press **Mount**.

Use **Unmount selected** before unplugging the drive. Closing the window unmounts everything.

## Formatting and erasing
Select a partition and press **Format / Erase…**. Choose a method:
- **Quick format** – writes a fresh empty file system (fast; old data stays on the disk but unreachable).
- **Full erase with zeros, then format** / **with random data, then format** – overwrites the whole area first (one pass).
- **Erase only with zeros** – overwrites the area and leaves it unformatted.

Full erases show a progress window with a Cancel button. Then choose FATX (original Xbox) or XTAF (Xbox 360) and a
cluster size (16 KB is the standard). You must type `ERASE` to confirm.
- For the original Xbox there is a **whole drive** option that recreates the standard X, Y, Z, C and E partitions.
- Formatting refuses Disk 0, anything below offset 0x80000 on a physical disk (the partition table area), and any disk
  that currently has a FatxMount volume mounted.
- An Xbox 360 console also needs a security sector on its drive that only the console writes. This tool formats the
  partition (normally Data360) of a drive the console has already set up; it cannot turn a blank PC drive into one the
  360 accepts as its own.

## Preparing a replacement drive (modded consoles)
**Prepare drive for console…** writes a whole standard partition layout to a blank or old drive, with a Quick or a Full
(zero-fill) method:
- **Original Xbox** – X, Y, Z, C and E (E up to 128 GiB; any space beyond stays unpartitioned). The drive must be unlocked,
  so this is for modded consoles. C is empty afterwards, so put your dashboard/BIOS files on it.
- **Xbox 360** – compatibility, cache and system partitions plus Data360. **Experimental:** the offsets are from memory
  of the standard layout and are not verified against a console. The security sector is not created, so this is for modded
  consoles; if the console rejects the drive, format it from the console's storage menu.

Nothing below offset 0x80000 is ever written.

## Journaling
All FAT and directory changes are written to a journal on this PC, flushed, and only then applied to the drive. After a
crash, the next writable mount replays the journal and the log shows how many changes were recovered. File contents are
written first, then the metadata that points to them. Journals live in `%LOCALAPPDATA%\FatxMount\journals`.
If you change the drive with another tool or a console after a crash, delete that folder's contents before mounting again.

## Large files
Setting a file's size is instant (new space is zeroed lazily), which fixes the "not enough system resources" error on
copies larger than about 2.5 GB. FATX itself cannot store files of 4 GiB or more.

## Command line (elevated terminal)
```
FatxMount list 2
FatxMount mount 2 --partition Data360 --drive Z
FatxMount mount 2 --offset 0x130EB0000 --drive Z --readonly
FatxMount mount 2 --partition E --drive Z --nojournal
FatxMount create-image test.img 128 [--x360]     # blank scratch image; mount it with --offset 0
```

## Troubleshooting
- **"Cannot open \\.\PhysicalDriveN for read/write"** (error 5): turn off Windows Security → Ransomware protection →
  Controlled folder access or allow `FatxMount.exe`; clear the disk's read-only flag (`diskpart`, `select disk N`,
  `attributes disk clear readonly`); pause antivirus; close other disk tools (error 32); check USB adapter write-protect (error 19).
- Windows may offer to initialize the Xbox disk. Decline; if writes are refused, set the disk Offline in Disk Management.
- Diagnostics are written to `%LOCALAPPDATA%\FatxMount\FatxMount.log`.

## Limits
42-character ASCII names, no ACLs, streams or links, no Recycle Bin (deletes are permanent).
