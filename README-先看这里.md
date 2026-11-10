# 谋杀谜题归类 demo（单向 A → B）

A 房间的人把线索便利贴归到分组面板上。B 房间的布局不同，B 看到 A 的化身在 B 自己的布局里走过去、拿起、放下同一张便利贴。

任务取自 Syiem et al. (2024, IJHCS) 用的 Stanford & Stanford 谋杀谜题，这里只放了 6 张便利贴和 2 个分组。

## 怎么看

双击 `01-Preview-With-AI-Relay.cmd`。会打开一个中转的命令行窗口和 A、B 两个窗口，把 A、B 并排放。

- **A 窗口**：按住鼠标左键拖一张便利贴，放到 Group 1 或 Group 2 上。左上角显示这张线索的全文。
- **B 窗口**：化身走到 Clue wall 拿起同一张，走到 West panel 或 East panel 放下。墙上黄字是中转发来的字幕。
- 你在 A 里还没松手时，化身已经跟着你悬停的面板走过去。
- Esc 退出窗口。中转的命令行窗口要手动关。

`02-Preview-Direct.cmd` 不经过中转，A 直接发给 B，没有字幕。

## 过程和意图分析

A 端发两种文本给中转：

- **事件**：抓起、放下、挪动、放回，每次一句。
- **轨迹采样**：每 0.4 秒一行原始数据，不带任何解释。手里有便利贴时是 `holding E [Bullet] | distance to Clue tray 0.76 m, Group 1 0.05 m, Group 2 1.52 m | moving toward Group 1 at 0.50 m/s | over Group 1`；手空时是 `hand empty | looking at D [Knife] on Clue tray`。

"犹豫"不再由规则判定，A 端不写任何 hesitation 句子。模型要自己从距离和运动的变化里读出来。

中转把两种文本改写成 B 的面板名。每次放下、挪动、放回之后，以及拿着便利贴期间每约 2.4 秒，把全部事件、最近 12 秒的轨迹、当前分组和上一次的解读发给模型，要求返回 JSON：`state`（exploring / deciding / confident / revising）、`confidence`、一句 `reading`、`torn_between`（正在哪几块面板之间拿不定）、每组的 `group_labels`、预测的下一张 `next`。墙上字幕显示状态、置信度和那一句解读，完整结果在 `relay-log.jsonl` 的 `intent` 行里。

B 端化身相应多了几个动作：

- A 看哪张便利贴，化身的头就转向 B 房间里的同一张；A 看超过半秒，化身走过去站在它前面看。
- 去拿便利贴时，视线先扫过旁边一张再落到目标上。
- A 拿着便利贴时，化身每帧重新决定去哪块面板，A 换面板它就改道；A 没悬停在任何面板上时，化身停下低头看手里的便利贴。
- 走路时头朝目的地，空闲时转回来看观察者。
- 模型返回的 `torn_between` 有两块面板时，化身拿着便利贴走到两块面板之间，每 1.6 秒转向其中一块、朝它迈一步。模型不再认为 A 犹豫，或 6 秒没有新的判断，就恢复成跟随 A 的悬停。
- 如果 A 犹豫的时候化身还没拿到便利贴，化身拿到后会先补演约 3 秒犹豫再放下。

化身的动作比 A 慢几秒（要走路）。A 犹豫得很快时，化身可能还在拿便利贴，这一段犹豫就只体现在字幕里，化身不会演出来。

## 三个部分

| 部分 | 位置 | 做什么 |
| --- | --- | --- |
| A 端 | `Unity/Assets/Scripts/SortDemo.cs` | 拖拽、把每次抓放写成一句文本、把本房间布局写成文本、每秒 20 次发完整状态 |
| 中转 | `relay/relay.py` | 收两边的布局文本，决定 A 的面板对应 B 的哪块，把状态和事件句子改写成 B 的面板名再转发 |
| B 端 | `SortDemo.cs` + `AvatarActor.cs` | 比较"A 的状态"和"B 房间现状"，让化身去补差：走路用 Mixamo 的 Idle/Walking，右臂用两段骨骼 IK 伸到便利贴位置 |

通信沿用指向 demo 的做法：UDP、序号去重、超时。发的是完整状态而不是单次事件，所以丢包不会让两边不一致。

## OpenAI

中转读环境变量 `OPENAI_API_KEY`，或 `relay/.env` 文件里的同名项。模型用 `OPENAI_MODEL`，默认 `gpt-5.4-mini`（和原语音后端一致）。

- **有 key**：面板对应表由模型根据两边的布局文本生成；每次放下便利贴后，字幕会多一句模型对 A 归类意图的解读。
- **没有 key**：面板按相同编号对应，字幕只有改写后的动作句子。

key 放在 `relay/.env` 里，这个文件不要提交或发给别人。带 key 的自测已跑通：对应表由模型返回，每次放下后约 1 到 2 秒出现意图解读。调用失败时会退回内置规则，错误写在 `relay/openai-errors.log`。

## 已验证和未验证

- **已验证**：三个进程（A、中转、B）在本机用脚本输入自动跑通，B 的最终摆放和 A 一致，有 key 和没有 key 两种情况都跑过；截图在 `smoke/`，中转日志在 `smoke/relay-log.jsonl`。
- **未验证**：两台电脑跨局域网、头显内运行。这个 demo 只有鼠标输入，没有接手部追踪。

## 改和重新打包

用 Unity 6000.0.46f1 打开 `Unity` 文件夹，菜单 `Mystery Sort / Generate scene and settings` 生成场景。布局、便利贴内容都在 `SortDemo.cs` 的 `BuildRoom` 和文件顶部的数组里，改完重新打包即可。

原来的 `VRPointingDemo-Source` 和 `QuestStandalone` 没有改动，这个工程是从 `VRPointingDemo-Source` 复制出来的。
