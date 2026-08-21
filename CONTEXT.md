# FancyToolAva

A desktop utility toolbox built on Avalonia. Contains grouped single-purpose tools for communication, encryption, hashing, encoding, and file/image processing.

## Language

**文件夹比较 / Folder Compare**:
A file tool that diffs two directory trees and reports per-file differences.
_Avoid_: 目录同步 (directory sync) — it never copies or modifies files.

**音乐标题提取 / Music Title Extraction**:
Reading the embedded tag `Title` of an audio file, falling back to the file name without extension when the tag is absent or unreadable.
_Avoid_: 媒体元数据 (media metadata) — the project only reads audio tag titles, never image metadata.

**SHA-256 哈希比较 / SHA-256 Hash Comparison**:
An optional folder-compare mode that treats two files as equal only when their SHA-256 digests match.
_Avoid_: 大小比较 (size comparison) — no size-based comparison mode exists.

## Relationships

- **文件夹比较** matches files by relative path, then optionally uses **SHA-256 哈希比较** or **音乐标题提取** as the equality criterion.
- **音乐标题提取** only applies to files whose extension is in the recognized audio list (`.mp3 .wav .flac .ogg .m4a .aac .opus .wma .aiff .ape`).

## Example dialogue

> **Dev:** "When I enable music-title comparison, which files does folder compare actually read?"
> **Domain expert:** "Only recognized audio extensions. For those it reads the embedded Title tag; files without a usable title fall back to their file name."

## Flagged ambiguities

- "媒体元数据" was once used to describe the media library's role, including for the Image → Base64 tool. Resolved: the project reads only audio tag metadata for **音乐标题提取**; the image tools do not read metadata.
- "大小比较" (size comparison) appears in older README copy but no such mode exists — the compare modes are relative-path matching plus optional hash / music-title comparison.
