# [OPEN] drag-tag-crash

## 症状
- 用户拖选/拖拽分配 Tag 时仍然闪退。
- 之前看到过 `task-scheduler` 的 `Unobserved task exception`，但该日志来自旧包，不能继续假设根因相同。

## 当前约束
- 在拿到新的运行证据前，不修改业务逻辑。
- 优先使用现有本地日志和崩溃日志，不再引入 `127.0.0.1` 调试上报。

## 可证伪假设
1. 拖拽分配 Tag 的后台任务抛出未观察异常，触发全局 task scheduler crash 记录。
2. 拖拽过程中修改集合或选区时发生跨线程/枚举期间修改异常。
3. Tag 分配写库过程中发生并发或约束异常，未在 UI 操作链路内捕获。
4. 拖拽目标或被拖拽书籍为空/已释放，导致 NullReference 或 InvalidOperation。
5. 当前实际运行的仍不是最新包，日志和源码版本不一致。

## 取证计划
- 读取最新 app/crash 日志，确认真实异常类型和堆栈。
- 定位 Tag 拖拽、批量分配、选区相关事件处理函数。
- 找到最小修复点后再改代码。

## 证据
- 最新日志：`DATA/logs/crash-20260623-190048-185.log`
- 真实异常：
  - `System.IO.IOException: 另一个程序已锁定文件的一部分，进程无法访问。 : '...\DATA\app.db-shm'`
  - 堆栈：`LibraryDatabase.CopyCompanionFile` -> `BackupDatabase` -> `SaveMetadata` -> `MainWindow.BooksList_Drop`
- 结论：拖拽分配 Tag 保存书籍元数据时触发数据库备份，备份 SQLite 运行期 `app.db-shm` 伴生文件被锁住，异常未被 Drop 链路捕获，导致 UI dispatcher 未处理异常并退出。

## 修复
- `LibraryDatabase` 备份时对 SQLite `-wal` / `-shm` 伴生文件复制改为 best-effort：锁定或无权限时跳过并记录 warning，不再中断元数据保存。
- `BooksList_Drop` 增加保存失败兜底：失败时回滚本次内存 Tag 修改、记录错误、更新状态栏并吞掉事件，避免 UI 未处理异常。
