import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * App 只有一個 outlet：工作區的外框是 `/app` 的父路由元件（WorkspaceLayoutComponent），不由 App 判斷要不要顯示，
 * 所以從非工作區換頁進出工作區時，路由頁面只建立一次（issue #322）。
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
  styleUrl: './app.scss',
})
export class App {}
