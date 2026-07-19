# Usage Finder — Complete Reference Finder (Cách B: grep-GUID 2 pha)

**Ngày:** 2026-07-19
**Trạng thái:** Approved (chờ user review spec)

## 1. Mục tiêu

Chọn 1 GameObject/asset → tìm **TẤT CẢ** field tham chiếu tới nó, **không sót case nào**:
list, object nested trong list, `AssetReference` (Addressables), và mọi trường ObjectReference/ExposedReference/ManagedReference ping được. Click 1 field → **điều hướng tới nơi chứa reference** (select object/asset chứa nó + highlight đúng field trong Inspector). Với scene file trên disk → **mở scene** rồi select + highlight.

## 2. Yêu cầu đã chốt (từ brainstorming)

| # | Quyết định |
|---|-----------|
| Click field | Điều hướng tới **nơi chứa** reference (Find Usages chuẩn), không phải ping lại target |
| Phạm vi khi target là asset | **Toàn project + scene đang mở** (không sót) |
| Scene file trên disk | Click → **tự động mở scene** rồi select + highlight field |
| Tổ chức tool | **1 lệnh duy nhất** → gộp mọi kết quả, không tab |
| Cơ chế phát hiện | **Cách B — grep GUID trong text file 2 pha** |

## 3. Tiền đề kỹ thuật (đã verify)

- `ProjectSettings/EditorSettings.asset` → `m_SerializationMode: 2` = **ForceText** → mọi asset là YAML text.
- Reference trong YAML: `{fileID: <long>, guid: <32 hex>, type: <int>}`. AssetReference lưu `m_AssetGUID: <32 hex>`. Cả hai đều chứa GUID target dạng text → grep bắt được.

## 4. Kiến trúc

```
UsageFinderWindow ("Find All Usages" — 1 nút, tự nhận diện loại target)
   │
   ├─ target là ASSET (AssetDatabase.GetAssetPath != rỗng, có GUID)
   │     └─► AssetReferenceScanner (MỚI — cách B 2 pha)
   │
   └─ target là SCENE OBJECT (không có GUID asset)
         └─► SceneReferenceScanner (giữ nguyên — đã field-level, match instanceID)
```

### 4.1 AssetReferenceScanner — luồng 2 pha

**Pha 1 — Candidate discovery (nhanh, chỉ đọc text — LƯỚI AN TOÀN):**
- Duyệt mọi asset path trong `Assets/` có đuôi có-thể-chứa-reference (`.prefab .asset .unity .mat .anim .controller .overrideController .playable .asset .preset .mask .physicMaterial .physicsMaterial2D .spriteatlas .terrainlayer .shadervariants .lighting .renderTexture ...` + fallback: mọi text asset).
- Đọc text file, tìm substring `guid: <targetGuid>` (32 hex). Match → file là **candidate**.
- Không load asset ở pha này → nhanh. Bọc `DisplayCancelableProgressBar` + cancel.
- Kết quả pha 1 = tập candidate path. Đây là **tập trên** (superset) đảm bảo không sót; pha 2 chỉ lọc/làm đẹp, không thêm nguồn mới.

**Pha 2 — Detail extraction (chỉ candidate — clickable):**
- Với mỗi candidate: load qua `AssetDatabase.LoadAllAssetsAtPath` (asset) hoặc mở-để-walk (scene → xem 4.3).
- Walk `SerializedObject` bằng `SerializedPropertyWalker`, so:
  - `ObjectReference`: `objectReferenceValue`'s asset GUID == targetGuid (hoặc là chính target khi cùng file).
  - `AssetReference`: `m_AssetGUID` == targetGuid.
  - `ExposedReference` / `ManagedReference`: descend, so như trên.
- Mỗi field khớp → 1 `UsageFieldHit`: `{ ownerLabel, fieldDisplayPath, propertyPath, navigation target }`.
- **Fallback:** nếu candidate khớp pha 1 nhưng pha 2 không tìm ra field cụ thể (kind lạ / sub-asset không load được) → vẫn tạo 1 hit mức-file ("chứa reference, không xác định field") để **không mất hit của pha 1**.

### 4.2 Phân loại referencer
- Asset (prefab/.asset/.mat/...) → load được → walk.
- Scene đang mở → walk trực tiếp GameObject roots (không cần mở lại).
- Scene file trên disk → pha 1 grep text bắt được; pha 2 **không** mở scene lúc scan (tránh destructive). Chỉ mở khi user click (4.3).

### 4.3 Navigation (click → điều hướng)
- **Asset field:** select asset chứa + `EditorApplication.delayCall` → expand đúng component/property (tái dùng `NavigationHelper`). Prefab → mở prefab stage nếu cần.
- **Scene đang mở:** select GameObject + expand field (đã có ở `NavigationHelper.SelectAndPingProperty`).
- **Scene file trên disk:** click → `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()` → `OpenScene(path)` → sau khi mở, tìm lại GameObject theo transform path (đã grep/parse ở pha 2 nếu có, hoặc re-walk) → select + expand. Lazy: chỉ mở khi click.

## 5. Data model

`UsageFieldHit` (mới hoặc mở rộng `UsageEntry.detailLabels` thành list object):
```
ownerLabel        // "GOName/Child (ComponentType)" hoặc "AssetName (SOType)"
fieldDisplayPath  // "Enemies[2] > Target" (BuildDisplayPath)
propertyPath      // raw, để expand Inspector
navKind           // Asset | OpenScene | DiskScene
navContext        // asset ref / (scenePath + transformPath + propertyPath)
```
`UsageEntry` giữ `assetPath + asset + List<UsageFieldHit>` (thay `List<string> detailLabels`). Drawer render field clickable thay vì label tĩnh.

## 6. Hiệu năng (SKILL #3)
- Pha 1 đọc text: dùng `File.ReadAllText` + `IndexOf(Ordinal)`; reuse buffer path list; không LINQ trong vòng nóng.
- Chỉ 1 lần I/O đọc text/ file ở pha 1; chỉ load asset cho candidate (thường rất ít).
- Cache display string tại scan time (giữ nguyên pattern zero-GC OnGUI).
- Progress bar + cancel cho cả 2 pha; cancel giữa chừng → đánh dấu `incomplete`, KHÔNG khẳng định "không ai dùng" (giữ nguyên chuẩn an toàn đã thêm ở review trước).

## 7. Edge cases
| Case | Xử lý |
|------|-------|
| Sub-asset (sprite trong atlas...) | Pha 1 bắt theo GUID cha; pha 2 walk mọi sub-object trong file |
| Target là chính file chứa (self-ref) | Bỏ qua self khi so |
| Reference qua `[SerializeReference]` nested | Walker descend managed ref; text grep là lưới an toàn |
| Scene chưa lưu | scene.path rỗng → chỉ hiện trong nhóm "scene đang mở" |
| File text nhưng không phải asset Unity | Chỉ quét path trong `Assets/` có đuôi hợp lệ; grep guid pattern nên nhiễu thấp |
| GUID xuất hiện trong comment/chuỗi lạ | Hiếm; pha 2 walk sẽ không tạo field → fallback file-level (chấp nhận, thà thừa còn hơn sót) |

## 8. Phạm vi giữ nguyên
- `SceneReferenceScanner` cho target-là-scene-object: giữ (đã field-level, đã sửa quét native component + all loaded scenes ở review trước).
- Các fix an toàn ("không khẳng định safe khi cancel/incomplete") ở review trước: giữ và áp cho scanner mới.

## 9. Không làm (YAGNI)
- Không index/cache kết quả grep giữa các lần scan (v1 chạy trực tiếp; tối ưu sau nếu chậm).
- Không quét ngoài `Assets/` (Packages read-only).
- `AssetReferenceIndex` cũ: giữ nguyên, không xóa; không còn là nguồn chính của tab này.
