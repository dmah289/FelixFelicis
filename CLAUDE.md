# CLAUDE.md

## Đọc trước khi làm bất cứ việc gì

**`Assets/Horcrux/MY_SKILL.md`** — tư tưởng thiết kế hệ thống và quy trình làm việc. Đọc **hết** trước
khi viết code, tài liệu, hay plan. Nó chứa: 12 nguyên tắc · quy trình phỏng vấn và chốt phạm vi · thiết kế
code · khi nào dùng toán · cách viết `.md` / `.html` / Plan.

File này **không** nhắc lại nội dung đó. Nó chỉ giữ thứ `MY_SKILL.md` cố ý không chứa để mang sang dự
án khác được: vị trí, tên, và layout riêng của dự án này.

## Vị trí

| Thứ | Ở đâu |
|---|---|
| Tư tưởng thiết kế & quy trình | `Assets/Horcrux/MY_SKILL.md` |
| Khung tài liệu `.html` | `Assets/Horcrux/DOCS_TEMPLATE.html` — cách dùng ghi trong khối comment đầu file |
| Horcrux SDK | `Assets/Horcrux` — **git submodule**, commit riêng |

Hai file đầu nằm **trong submodule**, nên chúng đi theo SDK sang dự án khác. File `CLAUDE.md` này ở gốc
repo và chỉ thuộc dự án này.

## Assembly

| Assembly | Thư mục | Namespace | References |
|---|---|---|---|
| `com.horcrux.runtime` | `Assets/Horcrux/Runtime/` | `Horcrux.Runtime` | InitArgs, InitArgs.Services, Unity.Addressables, Unity.ResourceManager, UniTask, UniTask.Addressables, Unity.Mathematics |
| `com.horcrux.editor` | `Assets/Horcrux/Editor/` | `Horcrux.Editor` | `com.horcrux.runtime`; `includePlatforms: [Editor]` |

## Layout — Runtime

```
Runtime/
├── Abstractions/      interface và abstract class
│   ├── Foundations/    hệ độc lập, bê sang dự án khác được
│   └── Composites/     hệ dựng trên nhiều Foundation
├── Implementations/   bản triển khai, soi gương theo tên hệ ở Abstractions/
│   ├── Foundations/
│   └── Composites/
└── Utilities/         static, universal, không phụ thuộc hệ nào trong SDK
```

Một hệ thường có mặt ở cả hai nhánh với **cùng tên thư mục**. Không phải cặp nào cũng đủ đôi: có hệ mới
chỉ có abstraction (chưa triển khai), có hệ triển khai trực tiếp vì không cần abstraction (`MY_SKILL.md`
NT6 — chỉ tạo abstraction khi có implementation thứ hai).

Phân loại Foundation so với Composite quyết **ngay khi tạo hệ**, không sửa sau (`MY_SKILL.md` §3.2).

## Layout — Editor

```
Editor/
├── Common/        dùng chung nhiều tool: màu, GUIContent, GUIStyle, layout options
├── Utilities/     helper cho editor
└── <TênTool>/     một thư mục một tool: window, drawer, data, utility của nó
```

## Đặt tài liệu ở đâu

`.md` và `.html` của một hệ thống nằm **trong chính thư mục hệ thống đó**, không gom vào thư mục docs
riêng. Ví dụ: `Runtime/Implementations/Foundations/Audio/AudioSystem.md`.

Nghĩa vụ cập nhật khi hệ thống đổi: xem `MY_SKILL.md` §5.
